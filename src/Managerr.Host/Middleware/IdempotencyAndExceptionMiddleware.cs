using System.Security.Cryptography;
using System.Text;
using Dapper;
using Managerr.Common.Net;
using Managerr.Datastore;
using Managerr.Host.Auth;

namespace Managerr.Host.Middleware;

public sealed class ProblemDetailsExceptionMiddleware(RequestDelegate next, ILogger<ProblemDetailsExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex) when (!context.Response.HasStarted)
        {
            var (status, code, title) = ex switch
            {
                ArgumentException => (StatusCodes.Status400BadRequest, "ERR_VALIDATION_FAILED", "Invalid request"),
                KeyNotFoundException => (StatusCodes.Status404NotFound, "ERR_NOT_FOUND", "Resource not found"),
                System.Security.SecurityException => (StatusCodes.Status401Unauthorized, "ERR_AUTH_FAILED", "Authentication verification failed"),
                UnauthorizedAccessException => (StatusCodes.Status403Forbidden, "ERR_RBAC_FORBIDDEN", "Access denied"),
                SsrfBlockedException => (StatusCodes.Status422UnprocessableEntity, "ERR_SSRF_EGRESS_BLOCKED", "Outbound connection blocked by SSRF guard"),
                InvalidOperationException => (StatusCodes.Status409Conflict, "ERR_CONFLICT", "Operation conflict"),
                _ => (StatusCodes.Status500InternalServerError, "ERR_INTERNAL", "Unexpected server error"),
            };

            if (status >= 500)
            {
                logger.LogError(ex, "Unhandled exception on {Method} {Path}", context.Request.Method, context.Request.Path);
            }

            context.Response.StatusCode = status;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsJsonAsync(new
            {
                type = $"https://managerr.dev/problems/{code.ToLowerInvariant()}",
                title,
                status,
                detail = status >= 500 ? "An unexpected internal error occurred." : ex.Message,
                code,
                traceId = context.TraceIdentifier,
            });
        }
    }
}

public sealed class IdempotencyMiddleware(RequestDelegate next, ISqliteDbFactory dbFactory)
{
    private const string HeaderName = "Idempotency-Key";

    public async Task InvokeAsync(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method) ||
            !context.Request.Headers.TryGetValue(HeaderName, out var keyValues))
        {
            await next(context);
            return;
        }

        var rawKey = keyValues.FirstOrDefault()?.Trim();
        if (string.IsNullOrWhiteSpace(rawKey))
        {
            await next(context);
            return;
        }

        var actorKey = context.Items.TryGetValue(ManagerrAuthConstants.PrincipalItemKey, out var pObj) && pObj is Managerr.Security.Auth.AuthenticatedPrincipal p
            ? (p.ApiKeyPrefix ?? $"user:{p.User?.Id ?? 0}")
            : $"anon:{context.Connection.RemoteIpAddress}";

        var composite = $"{actorKey}|{context.Request.Method}|{context.Request.Path}|{rawKey}";
        var keyHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(composite)));

        await using (var readConn = await dbFactory.OpenReadConnectionAsync(DatabaseKind.Core, context.RequestAborted))
        {
            var cached = await readConn.QuerySingleOrDefaultAsync<(int StatusCode, string ContentType, string Body, string ExpiresAt)>(
                """
                SELECT status_code AS StatusCode,
                       response_content_type AS ContentType,
                       response_body AS Body,
                       expires_at AS ExpiresAt
                FROM idempotency_records
                WHERE key_hash = @KeyHash;
                """,
                new { KeyHash = keyHash });

            if (cached != default && DateTimeOffset.TryParse(cached.ExpiresAt, out var exp) && exp > DateTimeOffset.UtcNow)
            {
                context.Response.StatusCode = cached.StatusCode;
                context.Response.ContentType = cached.ContentType;
                context.Response.Headers["X-Idempotency-Replayed"] = "true";
                await context.Response.WriteAsync(cached.Body, context.RequestAborted);
                return;
            }
        }

        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await next(context);

            buffer.Position = 0;
            var bodyText = await new StreamReader(buffer, Encoding.UTF8).ReadToEndAsync(context.RequestAborted);

            if (context.Response.StatusCode is >= 200 and < 300)
            {
                var now = DateTimeOffset.UtcNow;
                await dbFactory.ExecuteWriteAsync(
                    DatabaseKind.Core,
                    (conn, tx) => conn.ExecuteAsync(
                        """
                        INSERT OR REPLACE INTO idempotency_records (
                            key_hash, actor_key, method, path, status_code,
                            response_content_type, response_body, expires_at, created_at
                        ) VALUES (
                            @KeyHash, @ActorKey, @Method, @Path, @StatusCode,
                            @ContentType, @Body, @ExpiresAt, @CreatedAt
                        );
                        """,
                        new
                        {
                            KeyHash = keyHash,
                            ActorKey = actorKey,
                            Method = context.Request.Method,
                            Path = context.Request.Path.Value ?? "/",
                            StatusCode = context.Response.StatusCode,
                            ContentType = context.Response.ContentType ?? "application/json",
                            Body = bodyText,
                            ExpiresAt = now.AddHours(24).ToString("O"),
                            CreatedAt = now.ToString("O"),
                        },
                        transaction: tx),
                    context.RequestAborted);
            }

            buffer.Position = 0;
            await buffer.CopyToAsync(originalBody, context.RequestAborted);
        }
        finally
        {
            context.Response.Body = originalBody;
        }
    }
}
