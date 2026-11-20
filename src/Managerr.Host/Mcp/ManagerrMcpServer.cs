using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Managerr.Catalog;
using Managerr.Cleanerr;
using Managerr.Datastore.Services;
using Managerr.Downloaders;
using Managerr.Host.Auth;
using Managerr.Host.Endpoints;
using Managerr.Host.Services;
using Managerr.Indexers;
using Managerr.Metadata;
using Managerr.Migration;
using Managerr.Profiles;
using Managerr.Security.Auth;
using Managerr.Security.Rbac;
using Managerr.Verification;

namespace Managerr.Host.Mcp;

internal sealed record McpToolDefinition(
    string Name,
    string Description,
    IReadOnlyList<string> RequiredAnyScopes,
    JsonObject InputSchema,
    bool ReadOnlyHint,
    bool DestructiveHint,
    bool IdempotentHint,
    Func<AuthenticatedPrincipal, JsonElement, CancellationToken, Task<object?>> Handler);

public sealed partial class ManagerrMcpServer
{
    internal static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    private static readonly string[] SupportedProtocolVersions =
    [
        "2025-06-18",
        "2025-03-26",
        "2024-11-05",
    ];

    private readonly IPolymorphicCatalogService _catalog;
    private readonly IMetadataFederationService _federation;
    private readonly IComposableProfileService _profiles;
    private readonly IReleaseSearchOrchestrator _searchOrchestrator;
    private readonly IWantedAndActivityService _wantedService;
    private readonly IUnifiedDownloadOrchestrator _downloadOrchestrator;
    private readonly IUnifiedIndexerService _indexerService;
    private readonly ICompletedDownloadImportService _importService;
    private readonly IMediaManagementAndDiskScanService _diskScanService;
    private readonly ILanguageVerificationService _languageVerification;
    private readonly IFilesystemBrowseService _filesystemBrowse;
    private readonly ITaskManagerService _taskManager;
    private readonly IAuditLogService _audit;
    private readonly ICleanerrLifecycleEngine? _cleanerrEngine;
    private readonly IReadOnlyList<McpToolDefinition> _tools;
    private readonly IReadOnlyDictionary<string, McpToolDefinition> _toolsByName;

    public ManagerrMcpServer(
        IPolymorphicCatalogService catalog,
        IMetadataFederationService federation,
        IComposableProfileService profiles,
        IReleaseSearchOrchestrator searchOrchestrator,
        IWantedAndActivityService wantedService,
        IUnifiedDownloadOrchestrator downloadOrchestrator,
        IUnifiedIndexerService indexerService,
        ICompletedDownloadImportService importService,
        IMediaManagementAndDiskScanService diskScanService,
        ILanguageVerificationService languageVerification,
        IFilesystemBrowseService filesystemBrowse,
        ITaskManagerService taskManager,
        IAuditLogService audit,
        ICleanerrLifecycleEngine? cleanerrEngine = null)
    {
        _catalog = catalog;
        _federation = federation;
        _profiles = profiles;
        _searchOrchestrator = searchOrchestrator;
        _wantedService = wantedService;
        _downloadOrchestrator = downloadOrchestrator;
        _indexerService = indexerService;
        _importService = importService;
        _diskScanService = diskScanService;
        _languageVerification = languageVerification;
        _filesystemBrowse = filesystemBrowse;
        _taskManager = taskManager;
        _audit = audit;
        _cleanerrEngine = cleanerrEngine;

        _tools = BuildToolDefinitions();
        _toolsByName = _tools.ToDictionary(t => t.Name, StringComparer.Ordinal);
    }

    public async Task<JsonNode?> HandleRpcMessageAsync(
        JsonElement message,
        AuthenticatedPrincipal principal,
        HttpContext httpContext,
        CancellationToken ct)
    {
        if (message.ValueKind != JsonValueKind.Object)
        {
            return CreateJsonRpcError(null, -32600, "Invalid Request: expected JSON-RPC object.");
        }

        var hasId = message.TryGetProperty("id", out var idElement) &&
                    idElement.ValueKind != JsonValueKind.Undefined;

        if (!message.TryGetProperty("method", out var methodEl) ||
            methodEl.ValueKind != JsonValueKind.String)
        {
            return hasId ? CreateJsonRpcError(idElement, -32600, "Invalid Request: missing 'method'.") : null;
        }

        var method = methodEl.GetString()!;

        // Notifications omit "id": return null so HTTP layer replies 202 Accepted when no requests exist in batch.
        if (!hasId)
        {
            return null;
        }

        message.TryGetProperty("params", out var paramsElement);

        return method switch
        {
            "initialize" => HandleInitialize(idElement, paramsElement),
            "ping" => CreateJsonRpcResult(idElement, new JsonObject()),
            "tools/list" => HandleToolsList(idElement, principal),
            "tools/call" => await HandleToolsCallAsync(idElement, paramsElement, principal, httpContext, ct),
            "resources/list" => CreateJsonRpcResult(idElement, new JsonObject { ["resources"] = new JsonArray() }),
            "resources/templates/list" => CreateJsonRpcResult(idElement, new JsonObject { ["resourceTemplates"] = new JsonArray() }),
            "prompts/list" => CreateJsonRpcResult(idElement, new JsonObject { ["prompts"] = new JsonArray() }),
            _ => CreateJsonRpcError(idElement, -32601, $"Method '{method}' not found."),
        };
    }

    private static JsonObject HandleInitialize(JsonElement idElement, JsonElement paramsElement)
    {
        var requestedVersion = paramsElement.ValueKind == JsonValueKind.Object &&
                               paramsElement.TryGetProperty("protocolVersion", out var pv) &&
                               pv.ValueKind == JsonValueKind.String
            ? pv.GetString()
            : null;

        var negotiatedVersion = requestedVersion is not null &&
                                SupportedProtocolVersions.Contains(requestedVersion, StringComparer.Ordinal)
            ? requestedVersion
            : "2025-03-26";

        var (version, _, _) = SystemEndpoints.ResolveBuildVersionInfo();

        return CreateJsonRpcResult(idElement, new JsonObject
        {
            ["protocolVersion"] = negotiatedVersion,
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject { ["listChanged"] = false },
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = "managerr",
                ["title"] = "Managerr Media Automation MCP Server",
                ["version"] = version,
            },
            ["instructions"] =
                "Use Managerr tools to search, organize, monitor, grab releases, manage downloads, " +
                "verify audio/subtitle languages, and inspect quarantine across Movies, TV, Anime, Music, and Books.",
        });
    }

    private JsonObject HandleToolsList(JsonElement idElement, AuthenticatedPrincipal principal)
    {
        var list = new JsonArray();
        foreach (var tool in _tools)
        {
            if (!HasAnyScope(principal, tool.RequiredAnyScopes))
            {
                continue;
            }

            list.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["inputSchema"] = tool.InputSchema.DeepClone(),
                ["annotations"] = new JsonObject
                {
                    ["readOnlyHint"] = tool.ReadOnlyHint,
                    ["destructiveHint"] = tool.DestructiveHint,
                    ["idempotentHint"] = tool.IdempotentHint,
                },
            });
        }

        return CreateJsonRpcResult(idElement, new JsonObject { ["tools"] = list });
    }

    private async Task<JsonObject> HandleToolsCallAsync(
        JsonElement idElement,
        JsonElement paramsElement,
        AuthenticatedPrincipal principal,
        HttpContext httpContext,
        CancellationToken ct)
    {
        if (paramsElement.ValueKind != JsonValueKind.Object ||
            !paramsElement.TryGetProperty("name", out var nameEl) ||
            nameEl.ValueKind != JsonValueKind.String)
        {
            return CreateJsonRpcError(idElement, -32602, "Invalid params: 'name' is required.");
        }

        var toolName = nameEl.GetString()!;
        if (!_toolsByName.TryGetValue(toolName, out var tool))
        {
            return CreateJsonRpcError(idElement, -32602, $"Unknown tool '{toolName}'.");
        }

        if (!HasAnyScope(principal, tool.RequiredAnyScopes))
        {
            return CreateJsonRpcResult(idElement, CreateToolErrorPayload(
                $"Insufficient permissions for '{toolName}'. Required any of: {string.Join(", ", tool.RequiredAnyScopes)}.",
                "ERR_RBAC_FORBIDDEN"));
        }

        JsonElement arguments = default;
        if (paramsElement.TryGetProperty("arguments", out var argsEl) &&
            argsEl.ValueKind == JsonValueKind.Object)
        {
            arguments = argsEl;
        }
        else
        {
            using var emptyDoc = JsonDocument.Parse("{}");
            arguments = emptyDoc.RootElement.Clone();
        }

        try
        {
            var result = await tool.Handler(principal, arguments, ct);
            var text = JsonSerializer.Serialize(result, JsonOpts);
            return CreateJsonRpcResult(idElement, new JsonObject
            {
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = text,
                    },
                },
                ["isError"] = false,
            });
        }
        catch (UnauthorizedAccessException ex)
        {
            return CreateJsonRpcResult(idElement, CreateToolErrorPayload(ex.Message, "ERR_RBAC_FORBIDDEN"));
        }
        catch (KeyNotFoundException ex)
        {
            return CreateJsonRpcResult(idElement, CreateToolErrorPayload(ex.Message, "ERR_NOT_FOUND"));
        }
        catch (ArgumentException ex)
        {
            return CreateJsonRpcResult(idElement, CreateToolErrorPayload(ex.Message, "ERR_VALIDATION_FAILED"));
        }
        catch (InvalidOperationException ex)
        {
            return CreateJsonRpcResult(idElement, CreateToolErrorPayload(ex.Message, "ERR_CONFLICT"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return CreateJsonRpcResult(idElement, CreateToolErrorPayload(ex.Message, "ERR_TOOL_EXECUTION_FAILED"));
        }
    }

    private static bool HasAnyScope(AuthenticatedPrincipal principal, IEnumerable<string> requiredAnyScopes) =>
        principal.Scopes.Any(s => requiredAnyScopes.Contains(s, StringComparer.OrdinalIgnoreCase));

    private static void RequireAnyScope(AuthenticatedPrincipal principal, params string[] requiredAnyScopes)
    {
        if (!HasAnyScope(principal, requiredAnyScopes))
        {
            throw new UnauthorizedAccessException(
                $"Insufficient permissions. Required any scope of: {string.Join(", ", requiredAnyScopes)}.");
        }
    }

    private static JsonObject CreateJsonRpcResult(JsonElement idElement, JsonNode result) =>
        new()
        {
            ["jsonrpc"] = "2.0",
            ["id"] = JsonNode.Parse(idElement.GetRawText()),
            ["result"] = result,
        };

    internal static JsonObject CreateJsonRpcError(JsonElement? idElement, int code, string message) =>
        new()
        {
            ["jsonrpc"] = "2.0",
            ["id"] = idElement.HasValue && idElement.Value.ValueKind != JsonValueKind.Undefined
                ? JsonNode.Parse(idElement.Value.GetRawText())
                : null,
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message,
            },
        };

    private static JsonObject CreateToolErrorPayload(string message, string code) =>
        new()
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = JsonSerializer.Serialize(new { error = message, code }, JsonOpts),
                },
            },
            ["isError"] = true,
        };

    private static JsonObject ObjSchema(
        Action<JsonObject> configureProperties,
        params string[] required)
    {
        var props = new JsonObject();
        configureProperties(props);
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = props,
        };
        if (required.Length > 0)
        {
            var reqArr = new JsonArray();
            foreach (var r in required)
            {
                reqArr.Add(r);
            }
            schema["required"] = reqArr;
        }
        return schema;
    }

    private static JsonObject Prop(string type, string description, params string[] enumValues)
    {
        var obj = new JsonObject
        {
            ["type"] = type,
            ["description"] = description,
        };
        if (enumValues.Length > 0)
        {
            var arr = new JsonArray();
            foreach (var v in enumValues)
            {
                arr.Add(v);
            }
            obj["enum"] = arr;
        }
        return obj;
    }

    private static JsonObject ArrayProp(string itemType, string description) =>
        new()
        {
            ["type"] = "array",
            ["items"] = new JsonObject { ["type"] = itemType },
            ["description"] = description,
        };

    private static string? GetOptString(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var el))
        {
            return null;
        }
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.GetRawText(),
            _ => null,
        };
    }

    private static string GetReqString(JsonElement obj, string name)
    {
        var val = GetOptString(obj, name);
        if (string.IsNullOrWhiteSpace(val))
        {
            throw new ArgumentException($"Argument '{name}' is required and must be a non-empty string.");
        }
        return val.Trim();
    }

    private static long? GetOptLong(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var el))
        {
            return null;
        }
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var n))
        {
            return n;
        }
        if (el.ValueKind == JsonValueKind.String && long.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }
        return null;
    }

    private static long GetReqLong(JsonElement obj, string name) =>
        GetOptLong(obj, name) ?? throw new ArgumentException($"Argument '{name}' is required and must be an integer.");

    private static int? GetOptInt(JsonElement obj, string name) =>
        GetOptLong(obj, name) is { } l ? (int)l : null;

    private static bool? GetOptBool(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var el))
        {
            return null;
        }
        if (el.ValueKind == JsonValueKind.True) return true;
        if (el.ValueKind == JsonValueKind.False) return false;
        if (el.ValueKind == JsonValueKind.String && bool.TryParse(el.GetString(), out var b)) return b;
        return null;
    }

    private static IReadOnlyList<string>? GetOptStringArray(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        var list = new List<string>();
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s && !string.IsNullOrWhiteSpace(s))
            {
                list.Add(s.Trim());
            }
        }
        return list;
    }

    private static IReadOnlyList<long>? GetOptLongArray(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        var list = new List<long>();
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out var n))
            {
                list.Add(n);
            }
        }
        return list;
    }

    private static IReadOnlyDictionary<string, string>? GetOptStringDict(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in el.EnumerateObject())
        {
            var val = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.GetRawText();
            if (!string.IsNullOrWhiteSpace(val))
            {
                dict[p.Name] = val.Trim();
            }
        }
        return dict;
    }

    private IReadOnlyList<McpToolDefinition> BuildToolDefinitions()
    {
        var list = new List<McpToolDefinition>();
        RegisterCatalogTools(list);
        RegisterOperationsTools(list);
        RegisterCleanerrTools(list);
        return list;
    }
}
