using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Managerr.Common.Configuration;

public sealed class ManagerrAuthOptions
{
    public const string SectionName = "Managerr:Auth";

    public bool TrustForwardedHeaders { get; set; } = false;
    public List<string> TrustedProxyCidrs { get; set; } = [];
    public List<string> AllowedHosts { get; set; } = [];
    public int RateLimitPerMinute { get; set; } = 60;

    public LocalAuthOptions Local { get; set; } = new();
    public PasskeysAuthOptions Passkeys { get; set; } = new();
    public PlexAuthOptions Plex { get; set; } = new();
    public OidcAuthOptions Oidc { get; set; } = new();
}

public sealed class LocalAuthOptions
{
    public bool Enabled { get; set; } = true;
    public BootstrapAdminOptions? BootstrapAdmin { get; set; }
}

public sealed class BootstrapAdminOptions
{
    public string? Username { get; set; }
    public string? Email { get; set; }
    public string? DisplayName { get; set; }
    public string? Password { get; set; }
}

public sealed class PasskeysAuthOptions
{
    public bool Enabled { get; set; } = true;
    public string? RpId { get; set; }
    public string RpName { get; set; } = "Managerr";
    public string[]? Origins { get; set; }
    public bool RequireUserVerification { get; set; } = false;
}

public sealed class PlexAuthOptions
{
    public bool Enabled { get; set; } = true;
}

public sealed class OidcAuthOptions
{
    public bool Enabled { get; set; } = false;
    public string DisplayName { get; set; } = "Single Sign-On";
    public string? Issuer { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string Scopes { get; set; } = "openid profile email";
    public bool AutoProvision { get; set; } = true;
    public bool AutoRedirect { get; set; } = false;
    public bool TrustVerifiedEmail { get; set; } = false;
    public bool AllowUnclaimedOwnerBootstrap { get; set; } = true;
    public string UsernameClaim { get; set; } = "preferred_username";
    public string EmailClaim { get; set; } = "email";
    public string DisplayNameClaim { get; set; } = "name";
    public string GroupsClaim { get; set; } = "groups";
    public string[] AdminGroups { get; set; } = Array.Empty<string>();
    public string[] ManagerGroups { get; set; } = Array.Empty<string>();
    public string[] RequesterGroups { get; set; } = Array.Empty<string>();
}

public sealed class ManagerrUiOptions
{
    public const string SectionName = "Managerr:Ui";

    public string DefaultTheme { get; set; } = "dark";
    public string ColorScheme { get; set; } = "seafoam_jade";
    public string AccentColor { get; set; } = "#2dd4bf";
}

public sealed record ManagerrCustomThemePayload(
    string? Schema,
    string? Name,
    string? BaseMode,
    Dictionary<string, string>? Tokens)
{
    private static readonly JsonSerializerOptions WebJsonOpts = new(JsonSerializerDefaults.Web);

    public static ManagerrCustomThemePayload? FromJsonOrNull(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ManagerrCustomThemePayload>(rawJson, WebJsonOpts);
        }
        catch
        {
            return null;
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this, WebJsonOpts);
}
