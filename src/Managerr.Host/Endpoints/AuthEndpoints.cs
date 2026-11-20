using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Managerr.Common.Configuration;
using Managerr.Datastore.Services;
using Managerr.Host.Auth;
using Managerr.Security.Auth;
using Managerr.Security.Rbac;
using Managerr.Security.Users;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Managerr.Host.Endpoints;

public sealed record StartPlexPinRequest(string? ForwardUrl = null);
public sealed record HomeSwitchRequest(string TargetHomeUserUuid, string? Pin = null);
public sealed record BreakGlassSetupRequest(string NewPassword);
public sealed record BreakGlassLoginRequest(string? UsernameOrEmail = null, string? Username = null, string Password = "")
{
    public string EffectiveIdentifier => !string.IsNullOrWhiteSpace(UsernameOrEmail) ? UsernameOrEmail : (Username ?? "");
}
public sealed record LocalClaimOwnerRequest(string Username, string? Email = null, string? DisplayName = null, string Password = "");
public sealed record LocalLoginRequest(string? UsernameOrEmail = null, string? Username = null, string Password = "")
{
    public string EffectiveIdentifier => !string.IsNullOrWhiteSpace(UsernameOrEmail) ? UsernameOrEmail : (Username ?? "");
}
public sealed record UpdateUserProfileRequest(string? Username = null, string? DisplayName = null, string? Email = null);
public sealed record ChangePasswordRequest(string? CurrentPassword = null, string NewPassword = "");
public sealed record PasskeyRegisterOptionsRequest(bool? Promotion = null);
public sealed record PasskeyRegisterVerifyRequest(AuthenticatorAttestationRawResponse AttestationResponse, string? Name = null);
public sealed record RenamePasskeyRequest(string Name);
public sealed record CreateApiKeyRequest(
    string Name,
    IReadOnlyList<string> Scopes,
    IReadOnlyList<long>? AllowedLibraryIds = null,
    IReadOnlyList<string>? AllowedCidrs = null,
    int? ExpiresInDays = null);

public static class AuthEndpoints
{
    private const string OidcStateCookieName = "managerr_oidc_state";
    private const string AuthRateLimitPolicy = "auth_endpoints";
    private static readonly JsonSerializerOptions WebJsonOpts = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/auth").WithTags("Auth & Sessions");

        group.MapGet("/providers", async (
            HttpContext httpContext,
            IPlexAuthAndSessionService authService,
            IOidcAuthenticationService oidcService,
            IAppSettingsService appSettings,
            IConfigSourceInspector inspector,
            IOptions<ManagerrAuthOptions> authOptions,
            IOptions<ManagerrUiOptions> uiOptions,
            CancellationToken ct) =>
        {
            var setup = await authService.GetSetupStatusAsync(ct);
            var instanceName = await appSettings.GetAsync(GeneralSettingsKeys.InstanceName, ct) ?? "Managerr";
            var (defaultTheme, accentColor, colorScheme, customTheme) =
                await SystemEndpoints.ResolveEffectiveUiSettingsAsync(appSettings, inspector, uiOptions.Value, ct);

            var passkeyRpId = !string.IsNullOrWhiteSpace(authOptions.Value.Passkeys.RpId)
                ? authOptions.Value.Passkeys.RpId
                : (await appSettings.GetAsync("auth.passkeys.rp_id", ct) ?? httpContext.Request.Host.Host);

            var localEnabled = inspector.GetSource("local.enabled") != "database"
                ? authOptions.Value.Local.Enabled
                : (bool.TryParse(await appSettings.GetAsync("auth.local.enabled", ct), out var dbLocal) ? dbLocal : authOptions.Value.Local.Enabled);

            var passkeysEnabled = inspector.GetSource("passkeys.enabled") != "database"
                ? authOptions.Value.Passkeys.Enabled
                : (bool.TryParse(await appSettings.GetAsync("auth.passkeys.enabled", ct), out var dbPk) ? dbPk : authOptions.Value.Passkeys.Enabled);

            var oidcEffective = await oidcService.GetEffectiveConfigAsync(ct);

            var boundToken = await appSettings.GetSecretAsync(PlexSettingsKeys.OwnerAdminTokenEnc, ct)
                ?? await appSettings.GetAsync(PlexSettingsKeys.OwnerAdminTokenEnc, ct);
            var isPlexBound = !string.IsNullOrWhiteSpace(setup.BoundMachineId) && !string.IsNullOrWhiteSpace(boundToken);

            var plexEnabled = await IsPlexEnabledAsync(inspector, appSettings, authOptions, ct);

            return Results.Ok(new
            {
                isClaimed = setup.IsClaimed,
                instanceName,
                ui = new
                {
                    defaultTheme,
                    accentColor,
                    colorScheme,
                    customTheme,
                },
                local = new
                {
                    enabled = localEnabled,
                },
                passkeys = new
                {
                    enabled = passkeysEnabled,
                    rpId = passkeyRpId,
                },
                plex = new
                {
                    enabled = plexEnabled,
                    isBound = isPlexBound,
                    boundServerName = setup.BoundServerName,
                },
                oidc = new
                {
                    enabled = oidcEffective.Enabled,
                    displayName = oidcEffective.DisplayName,
                    autoRedirect = oidcEffective.AutoRedirect,
                },
            });
        }).AllowAnonymous();

        group.MapGet("/status", async (
            IPlexAuthAndSessionService authService,
            Managerr.Security.Plex.IPlexApiClient plexApi,
            CancellationToken ct) =>
        {
            var (isClaimed, boundServerName, boundMachineId) = await authService.GetSetupStatusAsync(ct);
            return Results.Ok(new
            {
                isClaimed,
                boundServerName,
                boundMachineId,
                plexClientIdentifier = plexApi.GetClientIdentifier(),
                authMode = "plex_primary_with_lan_breakglass",
            });
        }).AllowAnonymous();

        group.MapPost("/plex/pin", async (
            StartPlexPinRequest? req,
            HttpContext httpContext,
            IAppSettingsService appSettings,
            IConfigSourceInspector inspector,
            IOptions<ManagerrAuthOptions> authOptions,
            IPlexAuthAndSessionService authService,
            CancellationToken ct) =>
        {
            if (!await IsPlexEnabledAsync(inspector, appSettings, authOptions, ct))
            {
                return ProviderDisabledError("Plex", httpContext);
            }
            var pin = await authService.StartPlexPinAsync(req?.ForwardUrl, ct);
            return Results.Ok(pin);
        }).AllowAnonymous().RequireRateLimiting(AuthRateLimitPolicy);

        group.MapGet("/plex/callback", () =>
        {
            const string html = """
                <!doctype html>
                <html lang="en">
                <head>
                  <meta charset="utf-8" />
                  <meta name="viewport" content="width=device-width, initial-scale=1" />
                  <title>Managerr — Plex Sign-In Complete</title>
                  <style>
                    body { margin: 0; min-height: 100vh; display: flex; align-items: center; justify-content: center; background: #202020; color: #e1e2e3; font-family: Roboto, "Open Sans", "Helvetica Neue", Helvetica, Arial, sans-serif; }
                    .card { max-width: 360px; padding: 24px 28px; border-radius: 4px; background: #2a2a2a; border: 1px solid #393f45; text-align: center; box-shadow: 0 0 12px 1px #111111; }
                    h1 { margin: 0 0 8px; font-size: 17px; font-weight: 500; color: #e1e2e3; }
                    p { margin: 0 0 18px; font-size: 13px; color: #909293; line-height: 1.45; }
                    a { display: inline-block; padding: 7px 16px; border-radius: 4px; background: #5d9cec; color: #ffffff; font-size: 13px; font-weight: 500; text-decoration: none; }
                  </style>
                </head>
                <body>
                  <div class="card">
                    <h1>Plex Sign-In Approved</h1>
                    <p>Returning to Managerr. This window will close automatically.</p>
                    <a href="/" onclick="window.close(); return true;">Continue to Managerr</a>
                  </div>
                  <script>
                    (function () {
                      try {
                        if (typeof BroadcastChannel !== 'undefined') {
                          var bc = new BroadcastChannel('managerr-plex-auth');
                          bc.postMessage({ type: 'managerr-plex-auth-complete' });
                          bc.close();
                        }
                      } catch (_) {}
                      try {
                        if (window.opener && !window.opener.closed) {
                          window.opener.postMessage({ type: 'managerr-plex-auth-complete' }, window.location.origin);
                        }
                      } catch (_) {}
                      try { window.close(); } catch (_) {}
                      setTimeout(function () {
                        try { window.close(); } catch (_) {}
                      }, 150);
                    })();
                  </script>
                </body>
                </html>
                """;
            return Results.Content(html, "text/html; charset=utf-8");
        }).AllowAnonymous();

        group.MapGet("/plex/poll/{pinId:long}", async (
            long pinId,
            string? code,
            string? machineId,
            HttpContext httpContext,
            IAppSettingsService appSettings,
            IConfigSourceInspector inspector,
            IOptions<ManagerrAuthOptions> authOptions,
            IPlexAuthAndSessionService authService,
            CancellationToken ct) =>
        {
            if (!await IsPlexEnabledAsync(inspector, appSettings, authOptions, ct))
            {
                return ProviderDisabledError("Plex", httpContext);
            }

            var outcome = await authService.CompletePlexPinAsync(
                pinId: pinId,
                expectedCode: code,
                selectedMachineId: machineId,
                userAgent: httpContext.Request.Headers.UserAgent.ToString(),
                ipAddress: httpContext.Connection.RemoteIpAddress?.ToString(),
                ct: ct);

            if (!string.IsNullOrWhiteSpace(outcome.DeniedReason))
            {
                return Results.Json(
                    new
                    {
                        type = "https://managerr.dev/problems/err_plex_server_membership_denied",
                        title = "Plex server membership required",
                        status = StatusCodes.Status403Forbidden,
                        detail = outcome.DeniedReason,
                        code = "ERR_PLEX_SERVER_MEMBERSHIP_DENIED",
                        traceId = httpContext.TraceIdentifier,
                    },
                    contentType: "application/problem+json",
                    statusCode: StatusCodes.Status403Forbidden);
            }

            if (!outcome.Claimed)
            {
                return Results.Ok(new
                {
                    claimed = false,
                    requiresServerSelection = false,
                    authenticated = false,
                    availableOwnedServers = Array.Empty<object>(),
                });
            }

            if (outcome.RequiresServerSelection)
            {
                return Results.Ok(new
                {
                    claimed = true,
                    requiresServerSelection = true,
                    authenticated = false,
                    availableOwnedServers = outcome.AvailableOwnedServers.Select(s => new
                    {
                        s.Name,
                        s.MachineIdentifier,
                        s.Owned,
                        connections = s.Connections.Select(c => new { c.Uri, c.DirectUrl, c.Local, c.Protocol }),
                    }),
                });
            }

            var session = outcome.Session!;
            AppendSessionCookie(httpContext, session);

            return Results.Ok(new
            {
                claimed = true,
                requiresServerSelection = false,
                authenticated = true,
                csrfToken = session.CsrfToken,
                sessionToken = session.RawSessionToken,
                expiresAt = session.ExpiresAtUtc,
                user = session.User,
            });
        }).AllowAnonymous().RequireRateLimiting(AuthRateLimitPolicy);

        group.MapPost("/plex/home-switch", async (
            HomeSwitchRequest req,
            HttpContext httpContext,
            IAppSettingsService appSettings,
            IConfigSourceInspector inspector,
            IOptions<ManagerrAuthOptions> authOptions,
            IPlexAuthAndSessionService authService,
            IAuditLogService auditLog,
            CancellationToken ct) =>
        {
            if (!await IsPlexEnabledAsync(inspector, appSettings, authOptions, ct))
            {
                return ProviderDisabledError("Plex", httpContext);
            }

            var principal = httpContext.GetRequiredPrincipal();
            var actorId = principal.User?.Id ?? throw new UnauthorizedAccessException("Interactive Plex user session required.");

            var switched = await authService.SwitchHomeUserAsync(
                currentOwnerUserId: actorId,
                targetHomeUserUuid: req.TargetHomeUserUuid,
                pin: req.Pin,
                userAgent: httpContext.Request.Headers.UserAgent.ToString(),
                ipAddress: httpContext.Connection.RemoteIpAddress?.ToString(),
                ct: ct);

            if (httpContext.Items.TryGetValue(ManagerrAuthConstants.RawSessionTokenItemKey, out var oldTok) && oldTok is string oldRaw)
            {
                await authService.RevokeSessionAsync(oldRaw, ct);
            }

            AppendSessionCookie(httpContext, switched);
            await auditLog.RecordAsync(new AuditWriteRequest(
                Action: "auth.plex.home_switch",
                ResourceType: "user",
                ResourceId: switched.User.Id.ToString(),
                Details: new { FromUserId = actorId, ToUsername = switched.User.Username },
                ActorUserId: switched.User.Id,
                ActorPlexUserId: switched.User.PlexUserId,
                ActorUsername: switched.User.Username,
                IpAddress: httpContext.Connection.RemoteIpAddress?.ToString(),
                TraceId: httpContext.TraceIdentifier), ct);

            return Results.Ok(new
            {
                authenticated = true,
                csrfToken = switched.CsrfToken,
                sessionToken = switched.RawSessionToken,
                expiresAt = switched.ExpiresAtUtc,
                user = switched.User,
            });
        }).RequireAuthorization();

        group.MapPost("/breakglass/setup", async (
            BreakGlassSetupRequest req,
            HttpContext httpContext,
            IPlexUserService userService,
            IPlexAuthAndSessionService authService,
            IAuditLogService auditLog,
            CancellationToken ct) =>
        {
            var principal = httpContext.GetRequiredPrincipal();
            var user = principal.User ?? throw new UnauthorizedAccessException("User session required.");

            await userService.SetBreakGlassPasswordAsync(user.Id, req.NewPassword, ct);
            var currentTok = httpContext.Items.TryGetValue(ManagerrAuthConstants.RawSessionTokenItemKey, out var tok) ? tok as string : null;
            await authService.RevokeUserSessionsAsync(user.Id, currentTok, ct);
            await auditLog.RecordAsync(new AuditWriteRequest(
                Action: "auth.breakglass.configured",
                ResourceType: "user",
                ResourceId: user.Id.ToString(),
                ActorUserId: user.Id,
                ActorPlexUserId: user.PlexUserId,
                ActorUsername: user.Username,
                IpAddress: httpContext.Connection.RemoteIpAddress?.ToString(),
                TraceId: httpContext.TraceIdentifier), ct);

            return Results.Ok(new { configured = true });
        }).RequireScope(PermissionScopes.SystemAdmin);

        group.MapPost("/breakglass/login", async (
            BreakGlassLoginRequest req,
            HttpContext httpContext,
            IPlexUserService userService,
            IPlexAuthAndSessionService authService,
            IAuditLogService auditLog,
            CancellationToken ct) =>
        {
            var verified = await userService.VerifyBreakGlassLoginAsync(req.EffectiveIdentifier, req.Password, ct);
            if (verified is null)
            {
                await auditLog.RecordAsync(new AuditWriteRequest(
                    Action: "auth.breakglass.failed",
                    ResourceType: "user",
                    Details: new { AttemptedIdentifier = req.EffectiveIdentifier },
                    IpAddress: httpContext.Connection.RemoteIpAddress?.ToString(),
                    TraceId: httpContext.TraceIdentifier), ct);

                return Results.Json(
                    new
                    {
                        type = "https://managerr.dev/problems/err_invalid_credentials",
                        title = "Invalid break-glass credentials",
                        status = StatusCodes.Status401Unauthorized,
                        detail = "Invalid username/email or break-glass password.",
                        code = "ERR_INVALID_CREDENTIALS",
                        traceId = httpContext.TraceIdentifier,
                    },
                    contentType: "application/problem+json",
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            var session = await authService.CreateSessionForUserAsync(
                verified,
                httpContext.Request.Headers.UserAgent.ToString(),
                httpContext.Connection.RemoteIpAddress?.ToString(),
                ct);

            AppendSessionCookie(httpContext, session);
            await auditLog.RecordAsync(new AuditWriteRequest(
                Action: "auth.breakglass.login",
                ResourceType: "user",
                ResourceId: verified.Id.ToString(),
                ActorUserId: verified.Id,
                ActorPlexUserId: verified.PlexUserId,
                ActorUsername: verified.Username,
                IpAddress: httpContext.Connection.RemoteIpAddress?.ToString(),
                TraceId: httpContext.TraceIdentifier), ct);

            return Results.Ok(new
            {
                authenticated = true,
                csrfToken = session.CsrfToken,
                sessionToken = session.RawSessionToken,
                expiresAt = session.ExpiresAtUtc,
                user = session.User,
            });
        }).AllowAnonymous().RequireRateLimiting(AuthRateLimitPolicy);

        var handleLocalClaimOwner = async (
            LocalClaimOwnerRequest req,
            HttpContext httpContext,
            IPlexUserService userService,
            IPlexAuthAndSessionService sessionService,
            IAppSettingsService appSettings,
            IConfigSourceInspector inspector,
            IOptions<ManagerrAuthOptions> authOptions,
            IAuditLogService auditLog,
            CancellationToken ct) =>
        {
            if (!await IsLocalEnabledAsync(inspector, appSettings, authOptions, ct))
            {
                return ProviderDisabledError("Local", httpContext);
            }

            var user = await userService.ClaimLocalOwnerAsync(req.Username, req.Email, req.DisplayName, req.Password, ct);
            var userAgent = httpContext.Request.Headers.UserAgent.ToString();
            var ipAddress = httpContext.Connection.RemoteIpAddress?.ToString();
            var session = await sessionService.CreateSessionForUserAsync(user, userAgent, ipAddress, ct);
            AppendSessionCookie(httpContext, session);

            await auditLog.RecordAsync(new AuditWriteRequest(
                Action: "auth.local.owner_claimed",
                ResourceType: "user",
                ResourceId: user.Id.ToString(),
                Details: new { user.Username, user.Email },
                ActorUserId: user.Id,
                ActorUsername: user.Username,
                IpAddress: ipAddress,
                TraceId: httpContext.TraceIdentifier), ct);

            return Results.Ok(new
            {
                authenticated = true,
                claimed = true,
                sessionToken = session.RawSessionToken,
                csrfToken = session.CsrfToken,
                expiresAt = session.ExpiresAtUtc,
                user = session.User
            });
        };

        group.MapPost("/local/claim-owner", handleLocalClaimOwner).AllowAnonymous().RequireRateLimiting(AuthRateLimitPolicy);
        group.MapPost("/local/claim", handleLocalClaimOwner).AllowAnonymous().RequireRateLimiting(AuthRateLimitPolicy);

        group.MapPost("/local/login", async (
            LocalLoginRequest req,
            HttpContext httpContext,
            IPlexUserService userService,
            IPlexAuthAndSessionService sessionService,
            IAppSettingsService appSettings,
            IConfigSourceInspector inspector,
            IOptions<ManagerrAuthOptions> authOptions,
            IAuditLogService auditLog,
            CancellationToken ct) =>
        {
            if (!await IsLocalEnabledAsync(inspector, appSettings, authOptions, ct))
            {
                return ProviderDisabledError("Local", httpContext);
            }

            var ipAddress = httpContext.Connection.RemoteIpAddress?.ToString();
            var user = await userService.VerifyLocalLoginAsync(req.EffectiveIdentifier, req.Password, ct);
            if (user is null)
            {
                await auditLog.RecordAsync(new AuditWriteRequest(
                    Action: "auth.local.failed",
                    ResourceType: "user",
                    Details: new { AttemptedIdentifier = req.EffectiveIdentifier },
                    IpAddress: ipAddress,
                    TraceId: httpContext.TraceIdentifier), ct);

                return Results.Json(
                    new
                    {
                        type = "https://managerr.dev/problems/err_invalid_credentials",
                        title = "Invalid credentials",
                        status = StatusCodes.Status401Unauthorized,
                        detail = "Invalid username/email or password.",
                        code = "ERR_INVALID_CREDENTIALS",
                        traceId = httpContext.TraceIdentifier,
                    },
                    contentType: "application/problem+json",
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            var userAgent = httpContext.Request.Headers.UserAgent.ToString();
            var session = await sessionService.CreateSessionForUserAsync(user, userAgent, ipAddress, ct);
            AppendSessionCookie(httpContext, session);

            await auditLog.RecordAsync(new AuditWriteRequest(
                Action: "auth.local.login",
                ResourceType: "user",
                ResourceId: user.Id.ToString(),
                Details: new { user.Username },
                ActorUserId: user.Id,
                ActorUsername: user.Username,
                IpAddress: ipAddress,
                TraceId: httpContext.TraceIdentifier), ct);

            return Results.Ok(new
            {
                authenticated = true,
                sessionToken = session.RawSessionToken,
                csrfToken = session.CsrfToken,
                expiresAt = session.ExpiresAtUtc,
                user = session.User
            });
        }).AllowAnonymous().RequireRateLimiting(AuthRateLimitPolicy);

        group.MapPut("/profile", async (
            UpdateUserProfileRequest req,
            HttpContext httpContext,
            IPlexUserService userService,
            IAppSettingsService appSettings,
            IOptions<ManagerrAuthOptions> authOptions,
            CancellationToken ct) =>
        {
            var principal = httpContext.GetRequiredPrincipal();
            if (principal.User is null) return Results.Unauthorized();
            var targetUsername = !string.IsNullOrWhiteSpace(req.Username) ? req.Username : principal.User.Username;
            var updated = await userService.UpdateUserProfileAsync(principal.User.Id, targetUsername, req.DisplayName, req.Email, ct);

            var rpId = !string.IsNullOrWhiteSpace(authOptions.Value.Passkeys.RpId)
                ? authOptions.Value.Passkeys.RpId
                : (await appSettings.GetAsync("auth.passkeys.rp_id", ct) ?? httpContext.Request.Host.Host);

            return Results.Ok(new
            {
                updated.Id,
                updated.PlexUserId,
                updated.PlexUuid,
                updated.Username,
                updated.Email,
                updated.AvatarUrl,
                updated.IsServerOwner,
                updated.IsHomeUser,
                updated.IsActive,
                updated.HasBreakGlassPassword,
                updated.SharedPlexSectionIds,
                updated.PreferredAudioLang,
                updated.PreferredSubLang,
                updated.DiscordUserId,
                updated.DiscordUsername,
                updated.SeerrUserId,
                updated.Roles,
                updated.EffectivePermissions,
                updated.LastLoginAt,
                updated.CreatedAt,
                updated.DisplayName,
                updated.PasskeyUserId,
                updated.Providers,
                updated.PasskeyCount,
                user = updated,
                rpId,
                userId = updated.PasskeyUserId
            });
        }).RequireAuthorization();

        group.MapPost("/local/password", async (
            ChangePasswordRequest req,
            HttpContext httpContext,
            IPlexUserService userService,
            IPlexAuthAndSessionService sessionService,
            IAppSettingsService appSettings,
            IConfigSourceInspector inspector,
            IOptions<ManagerrAuthOptions> authOptions,
            IAuditLogService auditLog,
            CancellationToken ct) =>
        {
            if (!await IsLocalEnabledAsync(inspector, appSettings, authOptions, ct))
            {
                return ProviderDisabledError("Local", httpContext);
            }

            var principal = httpContext.GetRequiredPrincipal();
            if (principal.User is null) return Results.Unauthorized();
            await userService.ChangeLocalPasswordAsync(principal.User.Id, req.CurrentPassword ?? "", req.NewPassword, ct);

            var currentTok = httpContext.Items.TryGetValue(ManagerrAuthConstants.RawSessionTokenItemKey, out var tok) ? tok as string : null;
            await sessionService.RevokeUserSessionsAsync(principal.User.Id, currentTok, ct);

            await auditLog.RecordAsync(new AuditWriteRequest(
                Action: "auth.local.password_changed",
                ResourceType: "user",
                ResourceId: principal.User.Id.ToString(),
                ActorUserId: principal.User.Id,
                ActorUsername: principal.User.Username,
                IpAddress: httpContext.Connection.RemoteIpAddress?.ToString(),
                TraceId: httpContext.TraceIdentifier), ct);

            return Results.Ok(new { success = true });
        }).RequireAuthorization();

        var handleOidcStart = async (
            string? returnUrl,
            string? mode,
            HttpContext httpContext,
            IOidcAuthenticationService oidcService,
            CancellationToken ct) =>
        {
            long? linkUserId = null;
            if (string.Equals(mode, "link", StringComparison.OrdinalIgnoreCase))
            {
                if (!httpContext.Items.TryGetValue(ManagerrAuthConstants.PrincipalItemKey, out var pObj)
                    || pObj is not AuthenticatedPrincipal p
                    || p.User is null)
                {
                    return Results.Unauthorized();
                }
                linkUserId = p.User.Id;
                returnUrl ??= "/system/security";
            }

            var scheme = httpContext.Request.Scheme;
            var host = httpContext.Request.Host.Value;
            var redirectUri = $"{scheme}://{host}/api/v1/auth/oidc/callback";
            var url = await oidcService.BuildAuthorizationUrlAsync(redirectUri, returnUrl, linkUserId, ct);

            if (Uri.TryCreate(url, UriKind.Absolute, out var parsedUrl))
            {
                var queryParams = QueryHelpers.ParseQuery(parsedUrl.Query);
                if (queryParams.TryGetValue("state", out var stateVals) && !string.IsNullOrWhiteSpace(stateVals.ToString()))
                {
                    httpContext.Response.Cookies.Append(
                        OidcStateCookieName,
                        stateVals.ToString(),
                        new CookieOptions
                        {
                            HttpOnly = true,
                            SameSite = SameSiteMode.Lax,
                            Secure = httpContext.Request.IsHttps,
                            Path = "/api/v1/auth/oidc",
                            MaxAge = TimeSpan.FromMinutes(10),
                        });
                }
            }

            return Results.Redirect(url);
        };

        group.MapGet("/oidc/login", handleOidcStart).AllowAnonymous().RequireRateLimiting(AuthRateLimitPolicy);
        group.MapGet("/oidc/start", handleOidcStart).AllowAnonymous().RequireRateLimiting(AuthRateLimitPolicy);

        var handleOidcCallback = async (
            string? code,
            string? state,
            string? error,
            string? error_description,
            HttpContext httpContext,
            IOidcAuthenticationService oidcService,
            CancellationToken ct) =>
        {
            if (!string.IsNullOrEmpty(error))
            {
                return Results.BadRequest(new { error, error_description });
            }

            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
            {
                return Results.BadRequest(new { error = "invalid_request", error_description = "Missing code or state" });
            }

            if (!httpContext.Request.Cookies.TryGetValue(OidcStateCookieName, out var cookieState) ||
                string.IsNullOrWhiteSpace(cookieState) ||
                !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(cookieState),
                    Encoding.UTF8.GetBytes(state)))
            {
                return Results.BadRequest(new
                {
                    error = "invalid_state",
                    error_description = "Missing or mismatched OIDC state correlation cookie."
                });
            }

            httpContext.Response.Cookies.Delete(OidcStateCookieName, new CookieOptions
            {
                Path = "/api/v1/auth/oidc",
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Secure = httpContext.Request.IsHttps,
            });

            long? currentSessionUserId = null;
            if (httpContext.Items.TryGetValue(ManagerrAuthConstants.PrincipalItemKey, out var pObj) &&
                pObj is AuthenticatedPrincipal p &&
                p.User is not null)
            {
                currentSessionUserId = p.User.Id;
            }

            var scheme = httpContext.Request.Scheme;
            var host = httpContext.Request.Host.Value;
            var redirectUri = $"{scheme}://{host}/api/v1/auth/oidc/callback";
            var userAgent = httpContext.Request.Headers.UserAgent.ToString();
            var ipAddress = httpContext.Connection.RemoteIpAddress?.ToString();

            var (session, safeReturnUrl) = await oidcService.ProcessCallbackAsync(
                code,
                state,
                redirectUri,
                userAgent,
                ipAddress,
                currentSessionUserId,
                ct);
            AppendSessionCookie(httpContext, session);
            return Results.Redirect(safeReturnUrl);
        };

        group.MapGet("/oidc/callback", handleOidcCallback).AllowAnonymous().RequireRateLimiting(AuthRateLimitPolicy);
        group.MapPost("/oidc/callback", handleOidcCallback).AllowAnonymous().RequireRateLimiting(AuthRateLimitPolicy);

        group.MapPost("/passkeys/register/options", async (
            PasskeyRegisterOptionsRequest? req,
            HttpContext httpContext,
            IPasskeyAuthenticationService passkeyService,
            IAppSettingsService appSettings,
            IConfigSourceInspector inspector,
            IOptions<ManagerrAuthOptions> authOptions,
            CancellationToken ct) =>
        {
            if (!await IsPasskeysEnabledAsync(inspector, appSettings, authOptions, ct))
            {
                return ProviderDisabledError("Passkeys", httpContext);
            }

            var principal = httpContext.GetRequiredPrincipal();
            if (principal.User is null) return Results.Unauthorized();
            var host = httpContext.Request.Host.Value ?? "localhost";
            var scheme = httpContext.Request.Scheme ?? "http";
            var json = await passkeyService.GetRegistrationOptionsAsync(principal.User, req?.Promotion ?? false, host, scheme, ct);
            return Results.Content(json, "application/json");
        }).RequireAuthorization();

        group.MapPost("/passkeys/register/verify", async (
            JsonElement body,
            HttpContext httpContext,
            IPasskeyAuthenticationService passkeyService,
            IAppSettingsService appSettings,
            IConfigSourceInspector inspector,
            IOptions<ManagerrAuthOptions> authOptions,
            CancellationToken ct) =>
        {
            if (!await IsPasskeysEnabledAsync(inspector, appSettings, authOptions, ct))
            {
                return ProviderDisabledError("Passkeys", httpContext);
            }

            var principal = httpContext.GetRequiredPrincipal();
            if (principal.User is null) return Results.Unauthorized();

            string? customName = body.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String
                ? nameProp.GetString()
                : null;

            AuthenticatorAttestationRawResponse? attestationResponse;
            if (body.TryGetProperty("attestationResponse", out var nestedAtt) && nestedAtt.ValueKind == JsonValueKind.Object)
            {
                attestationResponse = nestedAtt.Deserialize<AuthenticatorAttestationRawResponse>(WebJsonOpts);
            }
            else
            {
                attestationResponse = body.Deserialize<AuthenticatorAttestationRawResponse>(WebJsonOpts);
            }

            if (attestationResponse is null)
            {
                return Results.BadRequest(new { error = "Invalid attestation payload." });
            }

            var host = httpContext.Request.Host.Value ?? "localhost";
            var scheme = httpContext.Request.Scheme ?? "http";
            var ipAddress = httpContext.Connection.RemoteIpAddress?.ToString();
            var dto = await passkeyService.VerifyRegistrationAsync(principal.User, attestationResponse, customName, host, scheme, ipAddress, ct);
            return Results.Ok(dto);
        }).RequireAuthorization();

        group.MapPost("/passkeys/login/options", async (
            HttpContext httpContext,
            IPasskeyAuthenticationService passkeyService,
            IAppSettingsService appSettings,
            IConfigSourceInspector inspector,
            IOptions<ManagerrAuthOptions> authOptions,
            CancellationToken ct) =>
        {
            if (!await IsPasskeysEnabledAsync(inspector, appSettings, authOptions, ct))
            {
                return ProviderDisabledError("Passkeys", httpContext);
            }

            var host = httpContext.Request.Host.Value ?? "localhost";
            var scheme = httpContext.Request.Scheme ?? "http";
            var json = await passkeyService.GetLoginOptionsAsync(host, scheme, ct);
            return Results.Content(json, "application/json");
        }).AllowAnonymous().RequireRateLimiting(AuthRateLimitPolicy);

        group.MapPost("/passkeys/login/verify", async (
            AuthenticatorAssertionRawResponse rawResponse,
            HttpContext httpContext,
            IPasskeyAuthenticationService passkeyService,
            IAppSettingsService appSettings,
            IConfigSourceInspector inspector,
            IOptions<ManagerrAuthOptions> authOptions,
            CancellationToken ct) =>
        {
            if (!await IsPasskeysEnabledAsync(inspector, appSettings, authOptions, ct))
            {
                return ProviderDisabledError("Passkeys", httpContext);
            }

            var host = httpContext.Request.Host.Value ?? "localhost";
            var scheme = httpContext.Request.Scheme ?? "http";
            var userAgent = httpContext.Request.Headers.UserAgent.ToString();
            var ipAddress = httpContext.Connection.RemoteIpAddress?.ToString();

            try
            {
                var (session, user) = await passkeyService.VerifyLoginAsync(rawResponse, host, scheme, userAgent, ipAddress, ct);
                AppendSessionCookie(httpContext, session);
                return Results.Ok(new
                {
                    authenticated = true,
                    sessionToken = session.RawSessionToken,
                    csrfToken = session.CsrfToken,
                    user
                });
            }
            catch (PasskeyNotFoundException ex)
            {
                return Results.NotFound(new { error = "ERR_PASSKEY_NOT_FOUND", rpId = ex.RpId });
            }
        }).AllowAnonymous().RequireRateLimiting(AuthRateLimitPolicy);

        group.MapGet("/passkeys", async (
            HttpContext httpContext,
            IPasskeyAuthenticationService passkeyService,
            CancellationToken ct) =>
        {
            var principal = httpContext.GetRequiredPrincipal();
            if (principal.User is null) return Results.Unauthorized();
            var host = httpContext.Request.Host.Value ?? "localhost";
            var result = await passkeyService.ListCredentialsAsync(principal.User, host, ct);
            return Results.Ok(result);
        }).RequireAuthorization();

        group.MapPut("/passkeys/{credentialId}", async (
            string credentialId,
            RenamePasskeyRequest req,
            HttpContext httpContext,
            IPasskeyAuthenticationService passkeyService,
            CancellationToken ct) =>
        {
            var principal = httpContext.GetRequiredPrincipal();
            if (principal.User is null) return Results.Unauthorized();
            try
            {
                var updated = await passkeyService.RenameCredentialAsync(principal.User, credentialId, req.Name, ct);
                return Results.Ok(updated);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        }).RequireAuthorization();

        group.MapDelete("/passkeys/{credentialId}", async (
            string credentialId,
            HttpContext httpContext,
            IPasskeyAuthenticationService passkeyService,
            IAppSettingsService appSettings,
            IOptions<ManagerrAuthOptions> authOptions,
            CancellationToken ct) =>
        {
            var principal = httpContext.GetRequiredPrincipal();
            if (principal.User is null) return Results.Unauthorized();
            try
            {
                await passkeyService.DeleteCredentialAsync(principal.User, credentialId, ct);
                var rpId = !string.IsNullOrWhiteSpace(authOptions.Value.Passkeys.RpId)
                    ? authOptions.Value.Passkeys.RpId
                    : (await appSettings.GetAsync("auth.passkeys.rp_id", ct) ?? httpContext.Request.Host.Host);
                return Results.Ok(new { deleted = true, rpId, userId = principal.User.PasskeyUserId });
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        }).RequireAuthorization();

        group.MapPost("/logout", async (
            HttpContext httpContext,
            IPlexAuthAndSessionService authService,
            CancellationToken ct) =>
        {
            if (httpContext.Items.TryGetValue(ManagerrAuthConstants.RawSessionTokenItemKey, out var tok) && tok is string rawTok)
            {
                await authService.RevokeSessionAsync(rawTok, ct);
            }

            httpContext.Response.Cookies.Delete(ManagerrAuthConstants.SessionCookieName, new CookieOptions
            {
                Path = "/",
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
            });

            return Results.NoContent();
        }).RequireAuthorization();

        group.MapGet("/me", (HttpContext httpContext) =>
        {
            var principal = httpContext.GetRequiredPrincipal();
            return Results.Ok(new
            {
                user = principal.User,
                csrfToken = principal.CsrfToken,
                scopes = principal.Scopes.OrderBy(s => s, StringComparer.Ordinal).ToArray(),
                isCookieSession = principal.IsCookieSession,
                apiKeyPrefix = principal.ApiKeyPrefix,
            });
        }).RequireAuthorization();

        group.MapGet("/tokens", async (
            bool? all,
            HttpContext httpContext,
            IPlexAuthAndSessionService authService,
            CancellationToken ct) =>
        {
            var principal = httpContext.GetRequiredPrincipal();
            var userId = principal.User?.Id ?? throw new UnauthorizedAccessException("User context required.");
            var canViewAll = principal.Scopes.Contains(PermissionScopes.SystemAdmin) && all == true;
            var keys = await authService.ListApiKeysAsync(canViewAll ? null : userId, ct);
            return Results.Ok(keys);
        }).RequireAuthorization();

        group.MapPost("/tokens", async (
            CreateApiKeyRequest req,
            HttpContext httpContext,
            IPlexAuthAndSessionService authService,
            IAuditLogService auditLog,
            CancellationToken ct) =>
        {
            var principal = httpContext.GetRequiredPrincipal();
            var user = principal.User ?? throw new UnauthorizedAccessException("Interactive user required to issue API keys.");

            DateTimeOffset? expiresAt = req.ExpiresInDays is > 0
                ? DateTimeOffset.UtcNow.AddDays(req.ExpiresInDays.Value)
                : null;

            var created = await authService.CreateApiKeyAsync(
                ownerUserId: user.Id,
                name: req.Name,
                scopes: req.Scopes,
                allowedLibraryIds: req.AllowedLibraryIds,
                allowedCidrs: req.AllowedCidrs,
                expiresAt: expiresAt,
                ct: ct);

            await auditLog.RecordAsync(new AuditWriteRequest(
                Action: "auth.apikey.created",
                ResourceType: "api_key",
                ResourceId: created.Id.ToString(),
                Details: new { created.Name, created.PublicPrefix, created.Scopes, created.AllowedCidrs },
                ActorUserId: user.Id,
                ActorPlexUserId: user.PlexUserId,
                ActorUsername: user.Username,
                IpAddress: httpContext.Connection.RemoteIpAddress?.ToString(),
                TraceId: httpContext.TraceIdentifier), ct);

            return Results.Created($"/api/v1/auth/tokens/{created.Id}", created);
        }).RequireAuthorization();

        group.MapDelete("/tokens/{id:long}", async (
            long id,
            HttpContext httpContext,
            IPlexAuthAndSessionService authService,
            IAuditLogService auditLog,
            CancellationToken ct) =>
        {
            var principal = httpContext.GetRequiredPrincipal();
            var user = principal.User ?? throw new UnauthorizedAccessException("User context required.");
            var allKeys = await authService.ListApiKeysAsync(null, ct);
            var target = allKeys.FirstOrDefault(k => k.Id == id)
                ?? throw new KeyNotFoundException($"API key #{id} not found.");

            if (target.OwnerUserId != user.Id && !principal.Scopes.Contains(PermissionScopes.SystemAdmin))
            {
                throw new UnauthorizedAccessException("Cannot revoke an API key owned by another user.");
            }

            await authService.RevokeApiKeyAsync(id, ct);
            await auditLog.RecordAsync(new AuditWriteRequest(
                Action: "auth.apikey.revoked",
                ResourceType: "api_key",
                ResourceId: id.ToString(),
                Details: new { target.PublicPrefix, target.Name },
                ActorUserId: user.Id,
                ActorPlexUserId: user.PlexUserId,
                ActorUsername: user.Username,
                IpAddress: httpContext.Connection.RemoteIpAddress?.ToString(),
                TraceId: httpContext.TraceIdentifier), ct);

            return Results.NoContent();
        }).RequireAuthorization();

        return app;
    }

    private static async Task<bool> IsAuthProviderEnabledAsync(
        IConfigSourceInspector inspector,
        IAppSettingsService appSettings,
        string sourceKey,
        string settingKey,
        bool fallback,
        CancellationToken ct)
    {
        if (inspector.GetSource(sourceKey) != "database")
        {
            return fallback;
        }
        return bool.TryParse(await appSettings.GetAsync(settingKey, ct), out var dbValue)
            ? dbValue
            : fallback;
    }

    private static Task<bool> IsLocalEnabledAsync(
        IConfigSourceInspector inspector,
        IAppSettingsService appSettings,
        IOptions<ManagerrAuthOptions> authOptions,
        CancellationToken ct) =>
        IsAuthProviderEnabledAsync(inspector, appSettings, "local.enabled", "auth.local.enabled", authOptions.Value.Local.Enabled, ct);

    private static Task<bool> IsPasskeysEnabledAsync(
        IConfigSourceInspector inspector,
        IAppSettingsService appSettings,
        IOptions<ManagerrAuthOptions> authOptions,
        CancellationToken ct) =>
        IsAuthProviderEnabledAsync(inspector, appSettings, "passkeys.enabled", "auth.passkeys.enabled", authOptions.Value.Passkeys.Enabled, ct);

    private static Task<bool> IsPlexEnabledAsync(
        IConfigSourceInspector inspector,
        IAppSettingsService appSettings,
        IOptions<ManagerrAuthOptions> authOptions,
        CancellationToken ct) =>
        IsAuthProviderEnabledAsync(inspector, appSettings, "plex.enabled", "auth.plex.enabled", authOptions.Value.Plex.Enabled, ct);

    private static IResult ProviderDisabledError(string providerName, HttpContext httpContext) =>
        Results.Json(new
        {
            type = "https://managerr.dev/problems/err_provider_disabled",
            title = "Authentication provider disabled",
            status = StatusCodes.Status403Forbidden,
            detail = $"{providerName} authentication is disabled on this Managerr instance.",
            code = "ERR_PROVIDER_DISABLED",
            traceId = httpContext.TraceIdentifier,
        }, contentType: "application/problem+json", statusCode: StatusCodes.Status403Forbidden);

    private static void AppendSessionCookie(HttpContext httpContext, IssuedSession session)
    {
        httpContext.Response.Cookies.Append(
            ManagerrAuthConstants.SessionCookieName,
            session.RawSessionToken,
            new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Secure = httpContext.Request.IsHttps,
                Path = "/",
                Expires = session.ExpiresAtUtc,
            });
    }
}
