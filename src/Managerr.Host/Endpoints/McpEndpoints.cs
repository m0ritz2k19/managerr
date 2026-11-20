using System.Text.Json;
using System.Text.Json.Nodes;
using Managerr.Common.Configuration;
using Managerr.Datastore.Services;
using Managerr.Host.Auth;
using Managerr.Host.Mcp;
using Microsoft.AspNetCore.Authorization;

namespace Managerr.Host.Endpoints;

public static class McpEndpoints
{
    public static IEndpointRouteBuilder MapMcpEndpoints(this IEndpointRouteBuilder app)
    {
        MapMcpRouteGroup(app, "/api/v1/mcp");
        MapMcpRouteGroup(app, "/mcp");
        return app;
    }

    private static void MapMcpRouteGroup(IEndpointRouteBuilder app, string prefix)
    {
        var authAttr = new AuthorizeAttribute { AuthenticationSchemes = ManagerrAuthConstants.SchemeName };

        app.MapPost(prefix, async (
            HttpContext httpContext,
            ManagerrMcpServer mcpServer,
            IAppSettingsService appSettings,
            IConfiguration configuration,
            IConfigSourceInspector inspector,
            CancellationToken ct) =>
        {
            if (!await IsMcpEnabledAsync(appSettings, configuration, inspector, ct))
            {
                await McpDisabledResult(httpContext).ExecuteAsync(httpContext);
                return;
            }

            var principal = httpContext.GetRequiredPrincipal();
            if (httpContext.Request.Headers.TryGetValue("Mcp-Session-Id", out var sessionIds) &&
                sessionIds.FirstOrDefault() is { Length: > 0 } sessionId)
            {
                httpContext.Response.Headers["Mcp-Session-Id"] = sessionId;
            }

            JsonDocument doc;
            try
            {
                doc = await JsonDocument.ParseAsync(httpContext.Request.Body, cancellationToken: ct);
            }
            catch (JsonException)
            {
                httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                await WriteJsonAsync(
                    httpContext,
                    ManagerrMcpServer.CreateJsonRpcError(null, -32700, "Parse error: invalid JSON."));
                return;
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Array)
                {
                    var responses = new JsonArray();
                    foreach (var item in root.EnumerateArray())
                    {
                        var reply = await mcpServer.HandleRpcMessageAsync(item, principal, httpContext, ct);
                        if (reply is not null)
                        {
                            responses.Add(reply);
                        }
                    }

                    if (responses.Count == 0)
                    {
                        httpContext.Response.StatusCode = StatusCodes.Status202Accepted;
                        return;
                    }

                    httpContext.Response.StatusCode = StatusCodes.Status200OK;
                    await WriteJsonAsync(httpContext, responses);
                    return;
                }

                if (root.ValueKind == JsonValueKind.Object)
                {
                    var reply = await mcpServer.HandleRpcMessageAsync(root, principal, httpContext, ct);
                    if (reply is null)
                    {
                        httpContext.Response.StatusCode = StatusCodes.Status202Accepted;
                        return;
                    }

                    httpContext.Response.StatusCode = StatusCodes.Status200OK;
                    await WriteJsonAsync(httpContext, reply);
                    return;
                }

                httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                await WriteJsonAsync(
                    httpContext,
                    ManagerrMcpServer.CreateJsonRpcError(null, -32600, "Invalid Request: expected JSON-RPC object or array."));
            }
        }).WithTags("MCP").RequireAuthorization(authAttr);

        app.MapGet(prefix, async (
            HttpContext httpContext,
            IAppSettingsService appSettings,
            IConfiguration configuration,
            IConfigSourceInspector inspector,
            CancellationToken ct) =>
        {
            if (!await IsMcpEnabledAsync(appSettings, configuration, inspector, ct))
            {
                return McpDisabledResult(httpContext);
            }

            httpContext.Response.Headers["Allow"] = "POST, DELETE";
            return Results.StatusCode(StatusCodes.Status405MethodNotAllowed);
        }).WithTags("MCP").RequireAuthorization(authAttr);

        app.MapDelete(prefix, async (
            HttpContext httpContext,
            IAppSettingsService appSettings,
            IConfiguration configuration,
            IConfigSourceInspector inspector,
            CancellationToken ct) =>
        {
            if (!await IsMcpEnabledAsync(appSettings, configuration, inspector, ct))
            {
                return McpDisabledResult(httpContext);
            }

            return Results.NoContent();
        })
            .WithTags("MCP")
            .RequireAuthorization(authAttr);
    }

    private static async Task<bool> IsMcpEnabledAsync(
        IAppSettingsService appSettings,
        IConfiguration configuration,
        IConfigSourceInspector inspector,
        CancellationToken ct)
    {
        if (inspector.GetSource("Managerr:Mcp:Enabled") != "database")
        {
            return configuration.GetValue("Managerr:Mcp:Enabled", true);
        }

        var raw = await appSettings.GetAsync("system.mcp.enabled", ct);
        return bool.TryParse(raw, out var enabled) ? enabled : configuration.GetValue("Managerr:Mcp:Enabled", true);
    }

    private static IResult McpDisabledResult(HttpContext httpContext) =>
        Results.Json(
            new
            {
                type = "https://managerr.dev/problems/err_mcp_disabled",
                title = "MCP server disabled",
                status = StatusCodes.Status404NotFound,
                detail = "The Managerr MCP server endpoint is disabled in system settings.",
                code = "ERR_MCP_DISABLED",
                traceId = httpContext.TraceIdentifier,
            },
            contentType: "application/problem+json",
            statusCode: StatusCodes.Status404NotFound);

    private static async Task WriteJsonAsync(HttpContext httpContext, JsonNode payload)
    {
        httpContext.Response.ContentType = "application/json; charset=utf-8";
        await httpContext.Response.WriteAsync(
            payload.ToJsonString(ManagerrMcpServer.JsonOpts),
            httpContext.RequestAborted);
    }
}
