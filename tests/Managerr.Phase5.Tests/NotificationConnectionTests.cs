using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Dapper;
using Managerr.Common.Notifications;
using Managerr.Datastore;
using Managerr.Host.Services;
using Managerr.Phase5.Tests.Support;
using Xunit;

namespace Managerr.Phase5.Tests;

/// <summary>
/// #67: notification connections. What each provider sends, that secrets never leave the server in plain text, which
/// events and tags trigger a connection, and that a failing connection never breaks anything else.
/// </summary>
public sealed class NotificationConnectionTests
{
    public static TheoryData<string, Dictionary<string, string>, string, string> Providers() => new()
    {
        { "discord", new() { ["webhookUrl"] = "https://discord.test/api/webhooks/1/SECRET-WEBHOOK" }, "POST", "https://discord.test/api/webhooks/1/SECRET-WEBHOOK" },
        { "telegram", new() { ["botToken"] = "123:SECRET-BOT", ["chatId"] = "42" }, "POST", "https://api.telegram.org/bot123:SECRET-BOT/sendMessage" },
        { "gotify", new() { ["serverUrl"] = "https://gotify.test", ["appToken"] = "SECRET-APP" }, "POST", "https://gotify.test/message?token=SECRET-APP" },
        { "ntfy", new() { ["serverUrl"] = "https://ntfy.test", ["topic"] = "managerr", ["accessToken"] = "SECRET-NTFY" }, "POST", "https://ntfy.test/managerr" },
        { "apprise", new() { ["apiUrl"] = "https://apprise.test/notify/abc" }, "POST", "https://apprise.test/notify/abc" },
        { "plex", new() { ["serverUrl"] = "https://plex.test:32400", ["token"] = "SECRET-PLEX", ["sectionId"] = "3" }, "GET", "https://plex.test:32400/library/sections/3/refresh" },
        { "jellyfin", new() { ["serverUrl"] = "https://jellyfin.test", ["apiKey"] = "SECRET-JF" }, "POST", "https://jellyfin.test/Library/Refresh" },
        { "webhook", new() { ["url"] = "https://hook.test/managerr", ["hmacSecret"] = "SECRET-HMAC" }, "POST", "https://hook.test/managerr" },
    };

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task EachProvider_SendsTheRequestItsApiExpects(string provider, Dictionary<string, string> settings, string method, string url)
    {
        var http = new RecordingHttp();
        await using var h = await HostTestHarness.StartAsync(http);
        var svc = h.Get<INotificationConnectionService>();

        var result = await svc.TestDraftAsync(new UpsertNotificationConnectionRequest($"Test {provider}", provider, settings));

        Assert.True(result.Success, result.Message);
        var request = Assert.Single(http.Requests);
        Assert.Equal(method, request.Method);
        Assert.Equal(url, request.Uri.ToString());
        Assert.DoesNotContain("SECRET", result.Message, StringComparison.Ordinal);

        switch (provider)
        {
            case "discord":
                Assert.Contains("Test", JsonNode.Parse(request.Body)!["embeds"]![0]!["title"]!.GetValue<string>());
                break;
            case "telegram":
                Assert.Equal("42", JsonNode.Parse(request.Body)!["chat_id"]!.ToString());
                break;
            case "ntfy":
                Assert.Equal("Bearer SECRET-NTFY", request.Headers["Authorization"]);
                Assert.True(request.Headers.ContainsKey("Title"));
                break;
            case "plex":
                Assert.Equal("SECRET-PLEX", request.Headers["X-Plex-Token"]);
                break;
            case "jellyfin":
                Assert.Equal("SECRET-JF", request.Headers["X-Emby-Token"]);
                break;
            case "webhook":
                var expected = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes("SECRET-HMAC"), Encoding.UTF8.GetBytes(request.Body)));
                Assert.Equal($"sha256={expected}", request.Headers["X-Managerr-Signature"]);
                break;
        }
    }

    [Fact]
    public async Task Secrets_AreEncryptedAtRest_MaskedWhenListed_AndKeptWhenTheMaskIsSentBack()
    {
        var http = new RecordingHttp();
        await using var h = await HostTestHarness.StartAsync(http);
        var svc = h.Get<INotificationConnectionService>();

        // A Discord webhook URL carries its token: whoever has it can post to the channel
        var discord = await svc.UpsertConnectionAsync(new UpsertNotificationConnectionRequest("Discord", "discord",
            new Dictionary<string, string> { ["webhookUrl"] = "https://discord.test/api/webhooks/1/SECRET-WEBHOOK", ["username"] = "Managerr" }));
        var telegram = await svc.UpsertConnectionAsync(new UpsertNotificationConnectionRequest("Telegram", "telegram",
            new Dictionary<string, string> { ["botToken"] = "123:SECRET-BOT", ["chatId"] = "42" }));

        var listed = await svc.ListConnectionsAsync();
        Assert.Equal("********", listed.Single(c => c.Id == discord.Id).Settings["webhookUrl"]);
        Assert.Equal("Managerr", listed.Single(c => c.Id == discord.Id).Settings["username"]);
        Assert.Equal("********", listed.Single(c => c.Id == telegram.Id).Settings["botToken"]);
        Assert.Equal("42", listed.Single(c => c.Id == telegram.Id).Settings["chatId"]);

        await using (var conn = await h.Get<ISqliteDbFactory>().OpenReadConnectionAsync(DatabaseKind.Core))
        {
            var stored = string.Join("\n", await conn.QueryAsync<string>("SELECT settings_json FROM notification_connections;"));
            Assert.DoesNotContain("SECRET", stored, StringComparison.Ordinal);
        }

        // The UI sends the mask back unchanged when only the name is edited
        await svc.UpsertConnectionAsync(new UpsertNotificationConnectionRequest("Discord renamed", "discord",
            new Dictionary<string, string> { ["webhookUrl"] = "********", ["username"] = "Managerr" }), discord.Id);
        var result = await svc.TestConnectionAsync(discord.Id);

        Assert.True(result.Success, result.Message);
        Assert.Equal("https://discord.test/api/webhooks/1/SECRET-WEBHOOK", http.Requests.Last().Uri.ToString());
    }

    [Fact]
    public async Task Events_TriggerOnlyMatchingConnections_AndTagsLimitMediaEvents()
    {
        var http = new RecordingHttp();
        await using var h = await HostTestHarness.StartAsync(http);
        var svc = h.Get<INotificationConnectionService>();
        await svc.UpsertConnectionAsync(new UpsertNotificationConnectionRequest("imports-only", "webhook",
            new Dictionary<string, string> { ["url"] = "https://hook.test/imports" }, OnGrab: false, OnImport: true));
        await svc.UpsertConnectionAsync(new UpsertNotificationConnectionRequest("anime-only", "webhook",
            new Dictionary<string, string> { ["url"] = "https://hook.test/anime" }, Tags: ["anime"]));
        await svc.UpsertConnectionAsync(new UpsertNotificationConnectionRequest("disabled", "webhook",
            new Dictionary<string, string> { ["url"] = "https://hook.test/disabled" }, IsEnabled: false));

        async Task<string[]> Fired(MediaLifecycleNotificationEvent ev)
        {
            http.Requests.Clear();
            await svc.PublishAsync(ev);
            return http.Requests.Select(r => r.Uri.AbsolutePath).Order().ToArray();
        }

        Assert.Empty(await Fired(new(MediaNotificationEventTypes.OnGrab, "grab", "m", WorkTags: ["tv"])));
        Assert.Equal(["/anime"], await Fired(new(MediaNotificationEventTypes.OnGrab, "grab", "m", WorkTags: ["anime"])));
        Assert.Equal(["/anime", "/imports"], await Fired(new(MediaNotificationEventTypes.OnImport, "import", "m", WorkTags: ["anime"])));
        Assert.Equal(["/imports"], await Fired(new(MediaNotificationEventTypes.OnImport, "import", "m", WorkTags: ["tv"])));

        // Health issues are not about one title, so tags do not filter them
        Assert.Equal(["/anime", "/imports"], await Fired(new(MediaNotificationEventTypes.OnHealthIssue, "health", "m")));
    }

    [Fact]
    public async Task AFailingConnection_IsReported_AndDoesNotStopTheOthers()
    {
        var http = new RecordingHttp
        {
            Responder = (req, _) => req.RequestUri!.AbsolutePath == "/broken"
                ? RecordingHttp.Json("""{"error":"boom"}""", HttpStatusCode.InternalServerError)
                : RecordingHttp.Json("{}"),
        };
        await using var h = await HostTestHarness.StartAsync(http);
        var svc = h.Get<INotificationConnectionService>();

        var draft = await svc.TestDraftAsync(new UpsertNotificationConnectionRequest("broken", "webhook",
            new Dictionary<string, string> { ["url"] = "https://hook.test/broken" }));
        Assert.False(draft.Success);
        Assert.Equal(500, draft.StatusCode);

        await svc.UpsertConnectionAsync(new UpsertNotificationConnectionRequest("broken", "webhook",
            new Dictionary<string, string> { ["url"] = "https://hook.test/broken" }));
        await svc.UpsertConnectionAsync(new UpsertNotificationConnectionRequest("working", "webhook",
            new Dictionary<string, string> { ["url"] = "https://hook.test/working" }));
        http.Requests.Clear();

        await svc.PublishAsync(new MediaLifecycleNotificationEvent(MediaNotificationEventTypes.OnImport, "import", "m"));

        Assert.Equal(["/broken", "/working"], http.Requests.Select(r => r.Uri.AbsolutePath));
    }

    [Fact]
    public async Task MissingRequiredSetting_IsAClearFailure()
    {
        await using var h = await HostTestHarness.StartAsync(new RecordingHttp());

        var result = await h.Get<INotificationConnectionService>().TestDraftAsync(
            new UpsertNotificationConnectionRequest("no chat", "telegram", new Dictionary<string, string> { ["botToken"] = "123:SECRET" }));

        Assert.False(result.Success);
        Assert.Contains("chatId", result.Message);
    }

    [Fact]
    public async Task CloudMetadataAddress_IsBlocked_ByTheRealEgressGuard()
    {
        // No HTTP fake: the production SafeHttpClientFactory refuses link-local / cloud metadata addresses
        await using var h = await HostTestHarness.StartAsync();

        var result = await h.Get<INotificationConnectionService>().TestDraftAsync(
            new UpsertNotificationConnectionRequest("metadata", "webhook",
                new Dictionary<string, string> { ["url"] = "http://169.254.169.254/latest/meta-data/" }));

        Assert.False(result.Success);
        Assert.Contains("169.254.169.254", result.Message);
    }
}
