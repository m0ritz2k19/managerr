using System.Security.Claims;
using System.Text.Encodings.Web;
using Managerr.Security.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Managerr.Host.Auth;

public static class ManagerrAuthConstants
{
    public const string SchemeName = "ManagerrZeroTrust";
    public const string SessionCookieName = "managerr_session";
    public const string CsrfHeaderName = "X-Managerr-Csrf";
    public const string ApiKeyHeaderName = "X-Api-Key";
    public const string PrincipalItemKey = "ManagerrPrincipal";
    public const string RawSessionTokenItemKey = "ManagerrRawSessionToken";
}

public sealed class ManagerrAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IPlexAuthAndSessionService authService)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var ct = Context.RequestAborted;
        AuthenticatedPrincipal? principal = null;
        string? rawSession = null;

        // 1. Check X-Api-Key header first
        if (Request.Headers.TryGetValue(ManagerrAuthConstants.ApiKeyHeaderName, out var apiKeyVals))
        {
            var rawKey = apiKeyVals.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(rawKey))
            {
                principal = await authService.ValidateApiKeyAsync(rawKey, Context.Connection.RemoteIpAddress, ct);
                if (principal is null)
                {
                    return AuthenticateResult.Fail("Invalid, expired, or CIDR-restricted API key.");
                }
            }
        }

        // 1b. Check ?apikey= query parameter on /compat/*, /feed/*, /api/v1/calendar/*, /api/v1/torznab/*, and MCP endpoints
        if (principal is null &&
            (Request.Path.StartsWithSegments("/compat") ||
             Request.Path.StartsWithSegments("/feed") ||
             Request.Path.StartsWithSegments("/api/v1/calendar") ||
             Request.Path.StartsWithSegments("/api/v1/torznab") ||
             Request.Path.StartsWithSegments("/api/v1/mcp") ||
             Request.Path.StartsWithSegments("/mcp")) &&
            Request.Query.TryGetValue("apikey", out var queryApiKeys))
        {
            var rawKey = queryApiKeys.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(rawKey))
            {
                principal = await authService.ValidateApiKeyAsync(rawKey, Context.Connection.RemoteIpAddress, ct);
                if (principal is null)
                {
                    return AuthenticateResult.Fail("Invalid, expired, or CIDR-restricted query API key.");
                }
            }
        }

        // 2. Check Authorization: Bearer <mgr_sess_* | mgr_live_*>
        if (principal is null && Request.Headers.TryGetValue("Authorization", out var authHeaders))
        {
            var bearer = authHeaders.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(bearer) && bearer.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                var token = bearer["Bearer ".Length..].Trim();
                if (token.StartsWith("mgr_live_", StringComparison.Ordinal))
                {
                    principal = await authService.ValidateApiKeyAsync(token, Context.Connection.RemoteIpAddress, ct);
                }
                else if (token.StartsWith("mgr_sess_", StringComparison.Ordinal))
                {
                    rawSession = token;
                    principal = await authService.ValidateSessionTokenAsync(token, isCookie: false, ct);
                }
                else
                {
                    principal = await authService.ValidateApiKeyAsync(token, Context.Connection.RemoteIpAddress, ct);
                    if (principal is null)
                    {
                        rawSession = token;
                        principal = await authService.ValidateSessionTokenAsync(token, isCookie: false, ct);
                    }
                }

                if (principal is null)
                {
                    return AuthenticateResult.Fail("Invalid or expired Bearer token.");
                }
            }
        }

        // 3. Check HttpOnly session cookie (or SignalR access_token query on /api/v1/realtime)
        if (principal is null && Request.Cookies.TryGetValue(ManagerrAuthConstants.SessionCookieName, out var cookieTok))
        {
            if (!string.IsNullOrWhiteSpace(cookieTok))
            {
                rawSession = cookieTok;
                principal = await authService.ValidateSessionTokenAsync(cookieTok, isCookie: true, ct);
                if (principal is null)
                {
                    return AuthenticateResult.Fail("Session cookie expired or revoked.");
                }
            }
        }

        if (principal is null &&
            Request.Path.StartsWithSegments("/api/v1/realtime") &&
            Request.Query.TryGetValue("access_token", out var wsTok))
        {
            var tok = wsTok.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(tok))
            {
                principal = await authService.ValidateSessionTokenAsync(tok, isCookie: false, ct);
            }
        }

        if (principal is null)
        {
            return AuthenticateResult.NoResult();
        }

        // Enforce CSRF token header on cookie-authenticated state-changing HTTP requests
        if (principal.IsCookieSession && IsMutatingMethod(Request.Method))
        {
            var providedCsrf = Request.Headers[ManagerrAuthConstants.CsrfHeaderName].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(providedCsrf) ||
                !string.Equals(providedCsrf, principal.CsrfToken, StringComparison.Ordinal))
            {
                return AuthenticateResult.Fail("Missing or invalid X-Managerr-Csrf header for cookie-authenticated mutation.");
            }
        }

        Context.Items[ManagerrAuthConstants.PrincipalItemKey] = principal;
        if (rawSession is not null)
        {
            Context.Items[ManagerrAuthConstants.RawSessionTokenItemKey] = rawSession;
        }

        var claims = new List<Claim>();
        if (principal.User is not null)
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, principal.User.Id.ToString()));
            claims.Add(new Claim(ClaimTypes.Name, principal.User.Username));
            claims.Add(new Claim("plex_user_id", principal.User.PlexUserId.ToString()));
        }
        if (principal.ApiKeyPrefix is not null)
        {
            claims.Add(new Claim("api_key_prefix", principal.ApiKeyPrefix));
        }
        foreach (var scope in principal.Scopes)
        {
            claims.Add(new Claim("scope", scope));
        }

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
        return AuthenticateResult.Success(ticket);
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.ContentType = "application/problem+json";
        await Response.WriteAsJsonAsync(new
        {
            type = "https://managerr.dev/problems/unauthorized",
            title = "Authentication required",
            status = StatusCodes.Status401Unauthorized,
            code = "ERR_AUTH_REQUIRED",
            traceId = Context.TraceIdentifier,
        });
    }

    protected override async Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        Response.ContentType = "application/problem+json";
        await Response.WriteAsJsonAsync(new
        {
            type = "https://managerr.dev/problems/forbidden",
            title = "Insufficient permissions",
            status = StatusCodes.Status403Forbidden,
            code = "ERR_RBAC_FORBIDDEN",
            traceId = Context.TraceIdentifier,
        });
    }

    private static bool IsMutatingMethod(string method) =>
        HttpMethods.IsPost(method) || HttpMethods.IsPut(method) || HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method);
}

public static class EndpointRbacExtensions
{
    public static TBuilder RequireScope<TBuilder>(this TBuilder builder, string requiredScope)
        where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization(policy =>
        {
            policy.AddAuthenticationSchemes(ManagerrAuthConstants.SchemeName);
            policy.RequireAuthenticatedUser();
            policy.RequireClaim("scope", requiredScope);
        });

    public static TBuilder RequireAnyScope<TBuilder>(this TBuilder builder, params string[] allowedScopes)
        where TBuilder : IEndpointConventionBuilder
    {
        if (allowedScopes is null || allowedScopes.Length == 0)
        {
            throw new ArgumentException("At least one scope is required.", nameof(allowedScopes));
        }

        return builder.RequireAuthorization(policy =>
        {
            policy.AddAuthenticationSchemes(ManagerrAuthConstants.SchemeName);
            policy.RequireAuthenticatedUser();
            policy.RequireClaim("scope", allowedScopes);
        });
    }

    public static AuthenticatedPrincipal GetRequiredPrincipal(this HttpContext context) =>
        context.Items.TryGetValue(ManagerrAuthConstants.PrincipalItemKey, out var obj) && obj is AuthenticatedPrincipal p
            ? p
            : throw new UnauthorizedAccessException("Authenticated principal is missing.");
}
