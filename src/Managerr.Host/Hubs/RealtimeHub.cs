using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Managerr.Common.Net;
using Managerr.Host.Auth;
using Managerr.Security.Rbac;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Managerr.Host.Hubs;

[Authorize(AuthenticationSchemes = ManagerrAuthConstants.SchemeName)]
public sealed class RealtimeHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        var scopes = Context.User?.FindAll("scope").Select(c => c.Value).ToHashSet(StringComparer.Ordinal) ?? [];
        foreach (var scope in scopes)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"scope:{scope}");
        }

        var uid = Context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (!string.IsNullOrEmpty(uid))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"user:{uid}");
        }

        await base.OnConnectedAsync();
    }
}

public interface IRealtimeBroadcaster
{
    ConcurrentDictionary<Guid, (HashSet<string> Scopes, Channel<(string EventName, string JsonPayload)> Channel)> SseSubscribers { get; }
    Task PublishScopedEventAsync(string requiredScope, string eventName, object payload, CancellationToken ct = default);
    (Guid SubscriptionId, ChannelReader<(string EventName, string JsonPayload)> Reader) SubscribeSse(IEnumerable<string> scopes);
    void UnsubscribeSse(Guid subscriptionId);
}

public sealed class RealtimeBroadcaster(IHubContext<RealtimeHub> hubContext) : IRealtimeBroadcaster
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<Guid, (HashSet<string> Scopes, Channel<(string EventName, string JsonPayload)> Channel)> _subscribers = new();

    public ConcurrentDictionary<Guid, (HashSet<string> Scopes, Channel<(string EventName, string JsonPayload)> Channel)> SseSubscribers => _subscribers;

    public async Task PublishScopedEventAsync(string requiredScope, string eventName, object payload, CancellationToken ct = default)
    {
        await hubContext.Clients.Group($"scope:{requiredScope}").SendAsync(eventName, payload, ct);

        if (_subscribers.IsEmpty)
        {
            return;
        }

        var json = payload is string s ? s : JsonSerializer.Serialize(payload, JsonOpts);
        foreach (var (_, (scopes, channel)) in _subscribers)
        {
            if (scopes.Contains(requiredScope))
            {
                channel.Writer.TryWrite((eventName, json));
            }
        }
    }

    public (Guid SubscriptionId, ChannelReader<(string EventName, string JsonPayload)> Reader) SubscribeSse(IEnumerable<string> scopes)
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<(string EventName, string JsonPayload)>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = false,
            SingleReader = true
        });
        var scopeSet = scopes.ToHashSet(StringComparer.Ordinal);
        _subscribers[id] = (scopeSet, channel);
        return (id, channel.Reader);
    }

    public void UnsubscribeSse(Guid subscriptionId)
    {
        if (_subscribers.TryRemove(subscriptionId, out var sub))
        {
            sub.Channel.Writer.TryComplete();
        }
    }
}

public sealed record SignedWebhookDeliveryResult(
    string DeliveryId,
    int StatusCode,
    string SignatureHeader);

public interface ISignedWebhookDispatcher
{
    (string SignatureHeader, string Timestamp, string DeliveryId, string JsonBody) SignPayload(object payload, string signingSecret);
    Task<SignedWebhookDeliveryResult> DispatchAsync(Uri targetUrl, object payload, string signingSecret, bool allowPrivateLan = false, CancellationToken ct = default);
}

public sealed class SignedWebhookDispatcher(ISafeHttpClientFactory httpFactory) : ISignedWebhookDispatcher
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public (string SignatureHeader, string Timestamp, string DeliveryId, string JsonBody) SignPayload(
        object payload,
        string signingSecret)
    {
        var deliveryId = Guid.NewGuid().ToString("D");
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var body = JsonSerializer.Serialize(payload, JsonOpts);
        var toSign = Encoding.UTF8.GetBytes($"{timestamp}.{body}");
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(signingSecret), toSign);
        var sigHeader = $"sha256={Convert.ToHexStringLower(mac)}";
        return (sigHeader, timestamp, deliveryId, body);
    }

    public async Task<SignedWebhookDeliveryResult> DispatchAsync(
        Uri targetUrl,
        object payload,
        string signingSecret,
        bool allowPrivateLan = false,
        CancellationToken ct = default)
    {
        var (sigHeader, timestamp, deliveryId, body) = SignPayload(payload, signingSecret);
        using var client = allowPrivateLan
            ? httpFactory.CreateHomelabClient(allowPrivateLan: true)
            : httpFactory.CreatePublicClient();

        using var req = new HttpRequestMessage(HttpMethod.Post, targetUrl)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("X-Managerr-Signature-256", sigHeader);
        req.Headers.TryAddWithoutValidation("X-Managerr-Timestamp", timestamp);
        req.Headers.TryAddWithoutValidation("X-Managerr-Delivery-Id", deliveryId);

        using var resp = await client.SendAsync(req, ct);
        return new SignedWebhookDeliveryResult(deliveryId, (int)resp.StatusCode, sigHeader);
    }
}
