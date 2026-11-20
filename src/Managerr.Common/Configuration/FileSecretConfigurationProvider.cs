using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;

namespace Managerr.Common.Configuration;

public sealed class FileSecretConfigurationProvider : ConfigurationProvider
{
    private readonly IDictionary _environmentVariables;

    public FileSecretConfigurationProvider(IDictionary? environmentVariables = null)
    {
        _environmentVariables = environmentVariables ?? Environment.GetEnvironmentVariables();
    }

    private const long MaxSecretFileBytes = 64 * 1024;

    private static bool IsAllowedPrefix(string key) =>
        key.StartsWith("Managerr__", StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith("MANAGERR_", StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith("SafeHttpEgress__", StringComparison.OrdinalIgnoreCase);

    public override void Load()
    {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (DictionaryEntry entry in _environmentVariables)
        {
            var key = entry.Key?.ToString();
            var filePath = entry.Value?.ToString();

            if (string.IsNullOrWhiteSpace(key) ||
                !key.EndsWith("_FILE", StringComparison.OrdinalIgnoreCase) ||
                !IsAllowedPrefix(key))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(filePath))
            {
                continue;
            }

            if (!File.Exists(filePath))
            {
                throw new InvalidOperationException($"Configuration file secret '{key}' specifies file '{filePath}' which does not exist.");
            }

            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Length > MaxSecretFileBytes)
            {
                throw new InvalidOperationException($"Configuration file secret '{key}' exceeds maximum size of {MaxSecretFileBytes} bytes.");
            }

            var secretContent = File.ReadAllText(filePath).TrimEnd('\r', '\n');
            var configKey = key[..^5];
            configKey = configKey.Replace("__", ":");

            data[configKey] = secretContent;
        }

        Data = data;
    }
}

public sealed class FileSecretConfigurationSource : IConfigurationSource
{
    private readonly IDictionary? _environmentVariables;

    public FileSecretConfigurationSource(IDictionary? environmentVariables = null)
    {
        _environmentVariables = environmentVariables;
    }

    public IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        return new FileSecretConfigurationProvider(_environmentVariables);
    }
}

public static class FileSecretConfigurationExtensions
{
    public static IConfigurationBuilder AddFileSecrets(this IConfigurationBuilder builder, IDictionary? environmentVariables = null)
    {
        return builder.Add(new FileSecretConfigurationSource(environmentVariables));
    }
}

public interface IConfigSourceInspector
{
    string GetSource(string configKey);
}

public sealed class ConfigSourceInspector : IConfigSourceInspector
{
    private readonly IConfiguration _configuration;
    private readonly string? _managerrJsonPath;
    private readonly IDictionary? _envVars;

    public ConfigSourceInspector(IConfiguration configuration, string? managerrJsonPath = null, IDictionary? envVars = null)
    {
        _configuration = configuration;
        _managerrJsonPath = managerrJsonPath;
        _envVars = envVars;
    }

    public string GetSource(string configKey)
    {
        if (string.IsNullOrWhiteSpace(configKey))
        {
            return "database";
        }

        var candidateKeys = GetCandidateKeys(configKey);

        // 1. Check environment / _FILE (including indexed children like Key__0 / Key__0_FILE)
        var env = _envVars ?? Environment.GetEnvironmentVariables();
        foreach (var candidate in candidateKeys)
        {
            var envKey = candidate.Replace(":", "__");
            if (env.Contains(envKey) && !string.IsNullOrEmpty(env[envKey]?.ToString()))
            {
                return "environment";
            }

            var envFileKey = envKey + "_FILE";
            if (env.Contains(envFileKey) && !string.IsNullOrEmpty(env[envFileKey]?.ToString()))
            {
                return "environment";
            }

            var indexedPrefix = envKey + "__";
            foreach (DictionaryEntry entry in env)
            {
                var k = entry.Key?.ToString();
                if (!string.IsNullOrEmpty(k) &&
                    k.StartsWith(indexedPrefix, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrEmpty(entry.Value?.ToString()))
                {
                    return "environment";
                }
            }
        }

        if (_configuration is IConfigurationRoot root)
        {
            foreach (var provider in root.Providers)
            {
                if (provider is FileSecretConfigurationProvider ||
                    provider.GetType().Name.Contains("EnvironmentVariablesConfigurationProvider", StringComparison.OrdinalIgnoreCase) ||
                    provider.GetType().Name.Contains("MemoryConfigurationProvider", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var candidate in candidateKeys)
                    {
                        if (ProviderHasKeyOrChildren(provider, candidate))
                        {
                            return "environment";
                        }
                    }
                }
            }
        }

        // 2. Check file (managerr.json)
        if (_configuration is IConfigurationRoot configRoot)
        {
            foreach (var provider in configRoot.Providers)
            {
                if (provider is JsonConfigurationProvider jsonProvider)
                {
                    var sourcePath = jsonProvider.Source.Path;
                    if (sourcePath != null && sourcePath.EndsWith("managerr.json", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var candidate in candidateKeys)
                        {
                            if (ProviderHasKeyOrChildren(provider, candidate))
                            {
                                return "file";
                            }
                        }
                    }
                }
            }
        }

        var jsonPath = _managerrJsonPath;
        if (!string.IsNullOrEmpty(jsonPath) && Directory.Exists(jsonPath))
        {
            jsonPath = Path.Combine(jsonPath, "managerr.json");
        }

        if (!string.IsNullOrEmpty(jsonPath) && File.Exists(jsonPath))
        {
            try
            {
                var json = File.ReadAllText(jsonPath);
                using var doc = JsonDocument.Parse(json);
                foreach (var candidate in candidateKeys)
                {
                    if (TryResolveJsonPathValue(doc.RootElement, candidate))
                    {
                        return "file";
                    }
                }
            }
            catch
            {
                // ignore malformed or unreadable override file
            }
        }

        return "database";
    }

    private static bool ProviderHasKeyOrChildren(IConfigurationProvider provider, string key)
    {
        if (provider.TryGet(key, out var val) && !string.IsNullOrEmpty(val))
        {
            return true;
        }

        foreach (var childKey in provider.GetChildKeys([], key))
        {
            var fullChild = $"{key}:{childKey}";
            if (provider.TryGet(fullChild, out var childVal) && !string.IsNullOrEmpty(childVal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryResolveJsonPathValue(JsonElement root, string candidateKey)
    {
        var segments = candidateKey.Split([':', '.'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return false;
        }

        var current = root;
        foreach (var segment in segments)
        {
            if (current.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var found = false;
            foreach (var prop in current.EnumerateObject())
            {
                if (string.Equals(prop.Name, segment, StringComparison.OrdinalIgnoreCase))
                {
                    current = prop.Value;
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                return false;
            }
        }

        return current.ValueKind switch
        {
            JsonValueKind.Undefined => false,
            JsonValueKind.Null => false,
            JsonValueKind.String => !string.IsNullOrWhiteSpace(current.GetString()),
            JsonValueKind.Array => current.GetArrayLength() > 0,
            _ => true,
        };
    }

    private static List<string> GetCandidateKeys(string key)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            key,
            key.Replace(".", ":"),
            key.Replace(":", ".")
        };

        var colonKey = key.Replace(".", ":");
        if (!colonKey.StartsWith("Managerr:", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add("Managerr:" + colonKey);
        }
        else
        {
            candidates.Add(colonKey["Managerr:".Length..]);
        }

        // Map settings keys e.g. auth.oidc.client_id -> Managerr:Auth:Oidc:ClientId
        var normalized = NormalizeKey(key);
        if (!string.IsNullOrEmpty(normalized))
        {
            candidates.Add(normalized);
            candidates.Add("Managerr:" + normalized);

            if (!normalized.StartsWith("Auth:", StringComparison.OrdinalIgnoreCase) &&
                (normalized.StartsWith("Oidc:", StringComparison.OrdinalIgnoreCase) ||
                 normalized.StartsWith("Passkeys:", StringComparison.OrdinalIgnoreCase) ||
                 normalized.StartsWith("Local:", StringComparison.OrdinalIgnoreCase) ||
                 normalized.StartsWith("Plex:", StringComparison.OrdinalIgnoreCase)))
            {
                candidates.Add("Managerr:Auth:" + normalized);
                candidates.Add("Auth:" + normalized);
            }
        }

        return candidates.ToList();
    }

    private static string NormalizeKey(string key)
    {
        var parts = key.Split(new[] { ':', '.' }, StringSplitOptions.RemoveEmptyEntries);
        var mapped = parts.Select(p =>
        {
            var subParts = p.Split('_', StringSplitOptions.RemoveEmptyEntries);
            return string.Concat(subParts.Select(sp => sp.Length > 0 ? char.ToUpperInvariant(sp[0]) + sp[1..] : ""));
        });
        return string.Join(":", mapped);
    }
}
