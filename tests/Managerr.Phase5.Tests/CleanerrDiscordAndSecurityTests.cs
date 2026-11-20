using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Managerr.Cleanerr;
using Managerr.Common.Net;
using Managerr.Datastore;
using Managerr.Datastore.Services;
using Managerr.Phase5.Tests.Support;
using Managerr.Security.Rbac;
using Managerr.Security.Users;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Managerr.Phase5.Tests;

public sealed class CleanerrDiscordAndSecurityTests
{
    private static readonly BigInteger Ed25519L =
        (BigInteger.One << 252) + BigInteger.Parse("27742317777372353535851937790883648493", CultureInfo.InvariantCulture);

    [Fact]
    public async Task Ed25519_Rfc8032Vectors_NonCanonicalScalarRejection_ReplayWindow_AndHttpInteractions()
    {
        // 1. RFC 8032 Section 7.1 Test Vector 1 (empty message)
        var seed1 = Convert.FromHexString("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60");
        const string pub1 = "d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a";
        const string sig1 = "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b";

        var (derivedPub1, _) = CleanerrCrypto.GenerateEd25519KeyPair(seed1);
        Assert.Equal(pub1, derivedPub1);
        Assert.Equal(sig1, CleanerrCrypto.SignEd25519(seed1, []));
        Assert.True(CleanerrCrypto.VerifyEd25519SignatureHex(pub1, sig1, []));

        // 2. RFC 8032 Section 7.1 Test Vector 2 (1-byte message 0x72)
        var seed2 = Convert.FromHexString("4ccd089b28ff96da9db6c346ec114e0f5b8a319f35aba624da8cf6ed4fb8a6fb");
        const string pub2 = "3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c";
        const string sig2 = "92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00";
        var msg2 = new byte[] { 0x72 };

        var (derivedPub2, _) = CleanerrCrypto.GenerateEd25519KeyPair(seed2);
        Assert.Equal(pub2, derivedPub2);
        Assert.Equal(sig2, CleanerrCrypto.SignEd25519(seed2, msg2));
        Assert.True(CleanerrCrypto.VerifyEd25519SignatureHex(pub2, sig2, msg2));

        // 3. RFC 8032 Section 7.1 Test Vector 3 (2-byte message 0xaf82)
        var seed3 = Convert.FromHexString("c5aa8df43f9f837bedb7442f31dcb7b166d38535076f094b85ce3a2e0b4458f7");
        const string pub3 = "fc51cd8e6218a1a38da47ed00230f0580816ed13ba3303ac5deb911548908025";
        const string sig3 = "6291d657deec24024827e69c3abe01a30ce548a284743a445e3680d7db5ac3ac18ff9b538d16f290ae67f760984dc6594a7c15e9716ed28dc027beceea1ec40a";
        var msg3 = new byte[] { 0xaf, 0x82 };

        var (derivedPub3, _) = CleanerrCrypto.GenerateEd25519KeyPair(seed3);
        Assert.Equal(pub3, derivedPub3);
        Assert.Equal(sig3, CleanerrCrypto.SignEd25519(seed3, msg3));
        Assert.True(CleanerrCrypto.VerifyEd25519SignatureHex(pub3, sig3, msg3));

        // 4. Non-canonical scalar rejection: S' = S + L (S' >= L)
        var validSigBytes = Convert.FromHexString(sig2);
        var sOrig = new BigInteger(validSigBytes.AsSpan(32, 32), isUnsigned: true, isBigEndian: false);
        var sNonCanonical = sOrig + Ed25519L;
        var sNonCanonicalBytes = sNonCanonical.ToByteArray(isUnsigned: true, isBigEndian: false);
        var malleableSigBytes = (byte[])validSigBytes.Clone();
        Array.Clear(malleableSigBytes, 32, 32);
        Buffer.BlockCopy(sNonCanonicalBytes, 0, malleableSigBytes, 32, Math.Min(32, sNonCanonicalBytes.Length));
        var malleableSigHex = Convert.ToHexStringLower(malleableSigBytes);

        Assert.False(CleanerrCrypto.VerifyEd25519SignatureHex(pub2, malleableSigHex, msg2));
        Assert.False(CleanerrCrypto.VerifyEd25519SignatureHex(pub2[..60], sig2, msg2));
        Assert.False(CleanerrCrypto.VerifyEd25519SignatureHex(pub2, sig2[..120], msg2));

        // 5. Timestamp replay window (default 300s)
        var refNow = DateTimeOffset.FromUnixTimeSeconds(1_760_000_000);
        var validTs = refNow.AddSeconds(-120).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var staleTs = refNow.AddSeconds(-301).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var futureTs = refNow.AddSeconds(301).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var pingBody = Encoding.UTF8.GetBytes("""{"type":1}""");

        var validSig = CleanerrCrypto.SignDiscordEd25519Payload(seed1, validTs, pingBody);
        var staleSig = CleanerrCrypto.SignDiscordEd25519Payload(seed1, staleTs, pingBody);
        var futureSig = CleanerrCrypto.SignDiscordEd25519Payload(seed1, futureTs, pingBody);

        Assert.True(CleanerrCrypto.VerifyDiscordEd25519Signature(pub1, validSig, validTs, pingBody, referenceNow: refNow));
        Assert.False(CleanerrCrypto.VerifyDiscordEd25519Signature(pub1, staleSig, staleTs, pingBody, referenceNow: refNow));
        Assert.False(CleanerrCrypto.VerifyDiscordEd25519Signature(pub1, futureSig, futureTs, pingBody, referenceNow: refNow));
        Assert.False(CleanerrCrypto.VerifyDiscordEd25519Signature(pub1, validSig, "invalid-ts", pingBody, referenceNow: refNow));

        // 6. HTTP POST /api/v1/cleanerr/webhooks/discord-interactions
        var tempDir = CreateTempDir();
        try
        {
            await using var factory = CreateWebFactory(tempDir);
            using var client = factory.CreateClient();
            using var scope = factory.Services.CreateScope();
            var discord = scope.ServiceProvider.GetRequiredService<ICleanerrDiscordService>();

            var (kpPub, kpSeed) = CleanerrCrypto.GenerateEd25519KeyPair();
            await discord.UpdateSettingsAsync(new UpdateCleanerrDiscordSettingsRequest(
                PublicKeyHex: kpPub,
                GatewayPassiveShadowMode: true));

            // Invalid signature -> 401
            using (var badReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/cleanerr/webhooks/discord-interactions")
            {
                Content = new ByteArrayContent(pingBody),
            })
            {
                badReq.Headers.Add("X-Signature-Ed25519", new string('b', 128));
                badReq.Headers.Add("X-Signature-Timestamp", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
                var badResp = await client.SendAsync(badReq);
                Assert.Equal(HttpStatusCode.Unauthorized, badResp.StatusCode);
            }

            // Stale timestamp -> 401
            var httpStaleTs = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            var httpStaleSig = CleanerrCrypto.SignDiscordEd25519Payload(kpSeed, httpStaleTs, pingBody);
            using (var staleReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/cleanerr/webhooks/discord-interactions")
            {
                Content = new ByteArrayContent(pingBody),
            })
            {
                staleReq.Headers.Add("X-Signature-Ed25519", httpStaleSig);
                staleReq.Headers.Add("X-Signature-Timestamp", httpStaleTs);
                var staleResp = await client.SendAsync(staleReq);
                Assert.Equal(HttpStatusCode.Unauthorized, staleResp.StatusCode);
            }

            // Valid PING (type=1) -> 200 OK {"type":1}
            var nowTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            var okSig = CleanerrCrypto.SignDiscordEd25519Payload(kpSeed, nowTs, pingBody);
            using (var okReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/cleanerr/webhooks/discord-interactions")
            {
                Content = new ByteArrayContent(pingBody),
            })
            {
                okReq.Headers.Add("X-Signature-Ed25519", okSig);
                okReq.Headers.Add("X-Signature-Timestamp", nowTs);
                var okResp = await client.SendAsync(okReq);
                Assert.Equal(HttpStatusCode.OK, okResp.StatusCode);
                var json = JsonNode.Parse(await okResp.Content.ReadAsStringAsync())!.AsObject();
                Assert.Equal(1, json["type"]!.GetValue<int>());
            }
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task MagicTokens_Expiry_Tampering_HtmlEncoding_AllMagicEndpoints_AndCrossInstanceSqlitePersistence()
    {
        // Unit checks on SignMagicToken / VerifyMagicToken
        const string unitSecret = "unit-magic-secret-value-for-hmac-test";
        var now = DateTimeOffset.UtcNow;
        var expiredTok = CleanerrCrypto.SignMagicToken(unitSecret, "exclude:42", now.AddMinutes(-5), nonce: "deadbeef");
        Assert.False(CleanerrCrypto.VerifyMagicToken(unitSecret, "exclude:42", expiredTok, now));

        var validTok = CleanerrCrypto.SignMagicToken(unitSecret, "exclude:42", now.AddHours(1), nonce: "a1b2c3d4");
        Assert.False(CleanerrCrypto.VerifyMagicToken(unitSecret, "exclude:99", validTok, now)); // target mismatch
        var tamperedTok = validTok[..^2] + (validTok.EndsWith("00", StringComparison.Ordinal) ? "ff" : "00");
        Assert.False(CleanerrCrypto.VerifyMagicToken(unitSecret, "exclude:42", tamperedTok, now));
        Assert.True(CleanerrCrypto.VerifyMagicToken(unitSecret, "exclude:42", validTok, now, consumeToken: false));

        var tempDir = CreateTempDir();
        try
        {
            await using var factory = CreateWebFactory(tempDir);
            using var client = factory.CreateClient();
            using var scope = factory.Services.CreateScope();

            var discord = scope.ServiceProvider.GetRequiredService<ICleanerrDiscordService>();
            var engine = scope.ServiceProvider.GetRequiredService<ICleanerrLifecycleEngine>();
            var settings = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();
            var userService = scope.ServiceProvider.GetRequiredService<IPlexUserService>();
            var dbFactory = scope.ServiceProvider.GetRequiredService<ISqliteDbFactory>();
            var httpGateway = scope.ServiceProvider.GetRequiredService<ICleanerrHttpGateway>();

            await discord.UpdateSettingsAsync(new UpdateCleanerrDiscordSettingsRequest(
                GatewayPassiveShadowMode: true,
                PublicBaseUrl: "https://managerr.lan"));

            var secret = await discord.GetMagicSecretAsync();

            // 1. exclude magic link + HTML encoding of XSS payload in title
            const string xssTitle = "<script>alert('xss')</script> Inception";
            var mExclude = await engine.UpsertMediaItemAsync(new UpsertCleanerrMediaItemRequest(
                "rk-magic-excl", "movie", xssTitle, Year: 2010, SizeBytes: 12_000_000_000));
            await engine.ManualStageItemsAsync(new ManualStageCleanerrItemsRequest([mExclude.Id], GraceDays: 7));

            var exclUrl = discord.BuildMagicExcludeUrl("", mExclude.Id, secret, DateTimeOffset.UtcNow.AddHours(1));
            var exclResp1 = await client.GetAsync(exclUrl);
            Assert.Equal(HttpStatusCode.OK, exclResp1.StatusCode);
            var exclHtml = await exclResp1.Content.ReadAsStringAsync();
            Assert.DoesNotContain("<script>alert('xss')</script>", exclHtml, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("&lt;script&gt;alert(&#39;xss&#39;)&lt;/script&gt;", exclHtml);
            Assert.Contains(await engine.ListExclusionRulesAsync(), r => r.MediaItemId == mExclude.Id);

            var exclResp2 = await client.GetAsync(exclUrl);
            Assert.Equal(HttpStatusCode.Forbidden, exclResp2.StatusCode);

            // 2. postpone magic link
            var mPostpone = await engine.UpsertMediaItemAsync(new UpsertCleanerrMediaItemRequest(
                "rk-magic-pp", "movie", "Interstellar", Year: 2014, SizeBytes: 22_000_000_000));
            await engine.ManualStageItemsAsync(new ManualStageCleanerrItemsRequest([mPostpone.Id], GraceDays: 7));

            var ppUrl = discord.BuildMagicPostponeUrl("", mPostpone.Id, 10, secret, DateTimeOffset.UtcNow.AddHours(1));
            var ppResp1 = await client.GetAsync(ppUrl);
            Assert.Equal(HttpStatusCode.OK, ppResp1.StatusCode);
            Assert.Equal(1, (await engine.ListDeletionQueueAsync("staged")).Single(q => q.MediaItemId == mPostpone.Id).PostponedCount);

            var ppResp2 = await client.GetAsync(ppUrl);
            Assert.Equal(HttpStatusCode.Forbidden, ppResp2.StatusCode);

            // 3. rescue magic link (using a whole "show" item)
            var mRescue = await engine.UpsertMediaItemAsync(new UpsertCleanerrMediaItemRequest(
                "rk-magic-rescue", "show", "Severance", Year: 2022, SizeBytes: 14_000_000_000));
            await engine.ManualStageItemsAsync(new ManualStageCleanerrItemsRequest([mRescue.Id], GraceDays: 7));

            var rescueUrl = discord.BuildMagicRescueUrl("", mRescue.Id, secret, DateTimeOffset.UtcNow.AddHours(1));
            var rescueResp1 = await client.GetAsync(rescueUrl);
            Assert.Equal(HttpStatusCode.OK, rescueResp1.StatusCode);
            Assert.Equal("rescued", (await engine.ListDeletionQueueAsync(null)).Single(q => q.MediaItemId == mRescue.Id).Status);

            var rescueResp2 = await client.GetAsync(rescueUrl);
            Assert.Equal(HttpStatusCode.Forbidden, rescueResp2.StatusCode);

            // 4. exclude-all magic link
            var mBulk = await engine.UpsertMediaItemAsync(new UpsertCleanerrMediaItemRequest(
                "rk-magic-excl-all", "movie", "Tenet", Year: 2020, SizeBytes: 26_000_000_000));
            await engine.ManualStageItemsAsync(new ManualStageCleanerrItemsRequest([mBulk.Id], GraceDays: 7));

            var exclAllUrl = discord.BuildMagicExcludeAllUrl("", null, secret, DateTimeOffset.UtcNow.AddHours(1));
            var exclAllResp1 = await client.GetAsync(exclAllUrl);
            Assert.Equal(HttpStatusCode.OK, exclAllResp1.StatusCode);
            Assert.Empty(await engine.ListDeletionQueueAsync("staged"));

            var exclAllResp2 = await client.GetAsync(exclAllUrl);
            Assert.Equal(HttpStatusCode.Forbidden, exclAllResp2.StatusCode);

            // 5. Cross-Instance SQLite Persistence Test
            const string crossTarget = "exclude:777888";
            var crossSig = CleanerrCrypto.SignMagicToken(secret, crossTarget, DateTimeOffset.UtcNow.AddHours(2));
            Assert.True(await discord.VerifyAndConsumeMagicTokenAsync(crossTarget, crossSig));

            var freshServiceInstance = new CleanerrDiscordService(settings, userService, httpGateway, dbFactory);
            Assert.False(await freshServiceInstance.VerifyAndConsumeMagicTokenAsync(crossTarget, crossSig));
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task StagedPayloadLayout_AndInteractiveComponentRouting_WorkEndToEnd()
    {
        var tempDir = CreateTempDir();
        try
        {
            await using var factory = CreateWebFactory(tempDir);
            using var scope = factory.Services.CreateScope();

            var discord = scope.ServiceProvider.GetRequiredService<ICleanerrDiscordService>();
            var engine = scope.ServiceProvider.GetRequiredService<ICleanerrLifecycleEngine>();

            await discord.UpdateSettingsAsync(new UpdateCleanerrDiscordSettingsRequest(
                GatewayPassiveShadowMode: true,
                AuthMode: "channel_members",
                PublicBaseUrl: "https://managerr.lan"));

            // Create 28 distinct whole items (26 movies + 2 shows) to verify 25-option dropdown cap & whole-series/movie formatting
            var mediaList = new List<CleanerrMediaItem>();
            for (var i = 1; i <= 26; i++)
            {
                var item = await engine.UpsertMediaItemAsync(new UpsertCleanerrMediaItemRequest(
                    $"rk-payload-{i}",
                    "movie",
                    $"Test Movie {i:D2}",
                    Year: 2000 + i,
                    SizeBytes: 10L * 1024 * 1024 * 1024));
                mediaList.Add(item);
            }

            var showAndor = await engine.UpsertMediaItemAsync(new UpsertCleanerrMediaItemRequest(
                "rk-show-1", "show", "Andor", Year: 2022, SizeBytes: 18_000_000_000));
            var showSeverance = await engine.UpsertMediaItemAsync(new UpsertCleanerrMediaItemRequest(
                "rk-show-2", "show", "Severance", Year: 2022, SizeBytes: 12_000_000_000));
            mediaList.Add(showAndor);
            mediaList.Add(showSeverance);

            var secret = await discord.GetMagicSecretAsync();

            // A. BuildDiscordStagedPayload with valid PublicBaseUrl
            var payloadWithUrl = discord.BuildDiscordStagedPayload(
                mediaList, graceDays: 7, "Leaving Soon", "https://managerr.lan", secret, workflowId: 55, workflowName: "Cleanup WF", pageIndex: 0);

            var rows = payloadWithUrl["components"]!.AsArray();
            Assert.Equal(5, rows.Count);

            // Row 1: multi-select dropdown with <= 25 unique values
            var selectComp = rows[0]!["components"]![0]!.AsObject();
            Assert.Equal(3, selectComp["type"]!.GetValue<int>());
            Assert.Equal("clnr_sel_excl:55:0", selectComp["custom_id"]!.GetValue<string>());
            var options = selectComp["options"]!.AsArray();
            Assert.True(options.Count <= 25);
            Assert.Equal(25, options.Count);
            var optionValues = options.Select(o => o!["value"]!.GetValue<string>()).ToList();
            Assert.Equal(optionValues.Count, optionValues.Distinct(StringComparer.Ordinal).Count());

            // Rows 2-3: Exclude buttons (clnr_excl:{id})
            var row2Btns = rows[1]!["components"]!.AsArray();
            var row3Btns = rows[2]!["components"]!.AsArray();
            Assert.All(row2Btns, b => Assert.StartsWith("clnr_excl:", b!["custom_id"]!.GetValue<string>(), StringComparison.Ordinal));
            Assert.All(row3Btns, b => Assert.StartsWith("clnr_excl:", b!["custom_id"]!.GetValue<string>(), StringComparison.Ordinal));

            // Row 4: Postpone buttons (clnr_postpone:{id}:7)
            var row4Btns = rows[3]!["components"]!.AsArray();
            Assert.All(row4Btns, b =>
            {
                var cid = b!["custom_id"]!.GetValue<string>();
                Assert.StartsWith("clnr_postpone:", cid, StringComparison.Ordinal);
                Assert.EndsWith(":7", cid, StringComparison.Ordinal);
            });

            // Row 5: pagination + excl_all + postpone_all + Cleanerr UI link button
            var row5Btns = rows[4]!["components"]!.AsArray();
            var customIds = row5Btns.Select(b => b!["custom_id"]?.GetValue<string>()).Where(s => s is not null).ToList();
            Assert.Contains(customIds, c => c!.StartsWith("clnr_page:55:", StringComparison.Ordinal));
            Assert.Contains("clnr_excl_all:55", customIds);
            Assert.Contains("clnr_postpone_all:55:7", customIds);
            Assert.Contains(row5Btns, b => b!["style"]?.GetValue<int>() == 5 && b["label"]?.GetValue<string>() == "Cleanerr UI" && b["url"]?.GetValue<string>() == "https://managerr.lan");

            // B. BuildDiscordStagedPayload with blank PublicBaseUrl omits Cleanerr UI link button
            var payloadNoUrl = discord.BuildDiscordStagedPayload(
                mediaList, graceDays: 7, "Leaving Soon", "", secret, workflowId: 55, workflowName: "Cleanup WF", pageIndex: 0);
            var row5NoUrl = payloadNoUrl["components"]!.AsArray()[4]!["components"]!.AsArray();
            Assert.DoesNotContain(row5NoUrl, b => b!["label"]?.GetValue<string>() == "Cleanerr UI");

            // C. Component routing via HandleDiscordInteractionPayloadAsync (delegated through engine, with both movies and shows)
            var stageSubset = mediaList.Take(10).Append(showAndor).Append(showSeverance).ToList();
            await engine.ManualStageItemsAsync(new ManualStageCleanerrItemsRequest(stageSubset.Select(m => m.Id).ToList(), GraceDays: 7));

            // Preview test interactions (non-mutating)
            var testBtnRes = await engine.HandleDiscordInteractionAsync(JsonNode.Parse("""
                {"type":3,"member":{"user":{"id":"101","username":"tester"}},"data":{"custom_id":"clnr_test_btn"}}
                """)!.AsObject());
            Assert.Contains("Interactive Button Works", testBtnRes.ToJsonString());

            var testSelRes = await engine.HandleDiscordInteractionAsync(JsonNode.Parse("""
                {"type":3,"member":{"user":{"id":"101","username":"tester"}},"data":{"custom_id":"clnr_test_sel","values":["test:1001"]}}
                """)!.AsObject());
            Assert.Contains("Interactive Dropdown Works", testSelRes.ToJsonString());
            Assert.Empty(await engine.ListExclusionRulesAsync());

            // clnr_sel_excl dropdown selection
            var selExclRes = await engine.HandleDiscordInteractionAsync(JsonNode.Parse($$$"""
                {"type":3,"member":{"user":{"id":"101","username":"tester"}},"message":{"id":"m-1"},"data":{"custom_id":"clnr_sel_excl:0:0","values":["{{{stageSubset[0].Id}}}"]}}
                """)!.AsObject());
            Assert.Equal(7, selExclRes["type"]!.GetValue<int>());
            Assert.Contains(await engine.ListExclusionRulesAsync(), r => r.MediaItemId == stageSubset[0].Id);

            // clnr_excl button
            var exclBtnRes = await engine.HandleDiscordInteractionAsync(JsonNode.Parse($$$"""
                {"type":3,"member":{"user":{"id":"101","username":"tester"}},"message":{"id":"m-1"},"data":{"custom_id":"clnr_excl:{{{stageSubset[1].Id}}}"}}
                """)!.AsObject());
            Assert.Equal(7, exclBtnRes["type"]!.GetValue<int>());
            Assert.Contains(await engine.ListExclusionRulesAsync(), r => r.MediaItemId == stageSubset[1].Id);

            // clnr_postpone button
            var postponeBtnRes = await engine.HandleDiscordInteractionAsync(JsonNode.Parse($$$"""
                {"type":3,"member":{"user":{"id":"101","username":"tester"}},"message":{"id":"m-1"},"data":{"custom_id":"clnr_postpone:{{{stageSubset[2].Id}}}:7"}}
                """)!.AsObject());
            Assert.Equal(7, postponeBtnRes["type"]!.GetValue<int>());
            Assert.Equal(1, (await engine.ListDeletionQueueAsync("staged")).Single(q => q.MediaItemId == stageSubset[2].Id).PostponedCount);

            // clnr_page pagination (type: 7 in-place update)
            var pageRes = await engine.HandleDiscordInteractionAsync(JsonNode.Parse("""
                {"type":3,"member":{"user":{"id":"101","username":"tester"}},"message":{"id":"m-1"},"data":{"custom_id":"clnr_page:0:0"}}
                """)!.AsObject());
            Assert.Equal(7, pageRes["type"]!.GetValue<int>());

            // clnr_postpone_all
            var postponeAllRes = await engine.HandleDiscordInteractionAsync(JsonNode.Parse("""
                {"type":3,"member":{"user":{"id":"101","username":"tester"}},"message":{"id":"m-1"},"data":{"custom_id":"clnr_postpone_all:0:7"}}
                """)!.AsObject());
            Assert.Equal(7, postponeAllRes["type"]!.GetValue<int>());
            Assert.Contains("Postponed", postponeAllRes["data"]!["embeds"]![0]!["description"]!.GetValue<string>());

            // clnr_excl_all
            var exclAllRes = await engine.HandleDiscordInteractionAsync(JsonNode.Parse("""
                {"type":3,"member":{"user":{"id":"101","username":"tester"}},"message":{"id":"m-1"},"data":{"custom_id":"clnr_excl_all:0"}}
                """)!.AsObject());
            Assert.Equal(7, exclAllRes["type"]!.GetValue<int>());
            Assert.Empty(await engine.ListDeletionQueueAsync("staged"));

            // clnr_del_excl (revoke exclusion rule)
            var ruleToDelete = (await engine.ListExclusionRulesAsync()).First();
            var delExclRes = await engine.HandleDiscordInteractionAsync(JsonNode.Parse($$$"""
                {"type":3,"member":{"user":{"id":"101","username":"tester"}},"data":{"custom_id":"clnr_del_excl","values":["{{{ruleToDelete.Id}}}"]}}
                """)!.AsObject());
            Assert.Contains("Revoked 1 Cleanerr exclusion rule", delExclRes.ToJsonString());
            Assert.DoesNotContain(await engine.ListExclusionRulesAsync(), r => r.Id == ruleToDelete.Id);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task SlashCommandsManifest_All7Subcommands_AndAutocomplete_ExecuteCorrectly()
    {
        // 1. Verify manifest has all 7 subcommands
        var manifest = CleanerrDiscordService.BuildSlashCommandsManifest();
        var rootCmd = manifest.Single()!.AsObject();
        Assert.Equal("cleanerr", rootCmd["name"]!.GetValue<string>());
        var subNames = rootCmd["options"]!.AsArray()
            .Select(o => o!["name"]!.GetValue<string>())
            .ToList();
        Assert.Equal(["status", "leaving-soon", "why", "exclude", "postpone", "exclusions", "run"], subNames);

        var tempDir = CreateTempDir();
        try
        {
            await using var factory = CreateWebFactory(tempDir);
            using var scope = factory.Services.CreateScope();

            var discord = scope.ServiceProvider.GetRequiredService<ICleanerrDiscordService>();
            var engine = scope.ServiceProvider.GetRequiredService<ICleanerrLifecycleEngine>();

            await discord.UpdateSettingsAsync(new UpdateCleanerrDiscordSettingsRequest(
                GatewayPassiveShadowMode: true,
                AuthMode: "channel_members",
                PublicBaseUrl: "https://managerr.lan"));

            var movie1 = await engine.UpsertMediaItemAsync(new UpsertCleanerrMediaItemRequest(
                "rk-sc-1", "movie", "Blade Runner 2049", Year: 2017, SizeBytes: 24L * 1024 * 1024 * 1024,
                LastViewedAt: DateTimeOffset.UtcNow.AddDays(-200).ToString("O"), ViewCount: 1));
            var movie2 = await engine.UpsertMediaItemAsync(new UpsertCleanerrMediaItemRequest(
                "rk-sc-2", "movie", "Dune: Part Two", Year: 2024, SizeBytes: 30L * 1024 * 1024 * 1024,
                LastViewedAt: DateTimeOffset.UtcNow.AddDays(-100).ToString("O"), ViewCount: 2));

            await engine.ManualStageItemsAsync(new ManualStageCleanerrItemsRequest([movie1.Id, movie2.Id], GraceDays: 7));

            var wf = await engine.UpsertWorkflowAsync(new UpsertCleanerrWorkflowRequest(
                Name: "Slash Run Workflow",
                Enabled: true,
                DryRun: true,
                GraphJson: JsonNode.Parse("""
                    {
                      "nodes": [
                        { "id": "src", "data": { "label": "Movies", "node_type": "source.plex_library", "config": { "media_type": "movie" } } },
                        { "id": "g", "data": { "label": "Grace", "node_type": "queue.grace_period", "config": { "grace_days": 7 } } }
                      ],
                      "edges": [
                        { "source": "src", "target": "g" }
                      ]
                    }
                    """)!.AsObject()));

            // 2. Autocomplete (type: 4 -> type: 8) across why, exclude, postpone, run
            foreach (var acSub in new[] { "why", "exclude", "postpone" })
            {
                var ac = await engine.HandleDiscordInteractionAsync(JsonNode.Parse($$$"""
                    {"type":4,"member":{"user":{"id":"201","username":"homelab"}},"data":{"name":"cleanerr","options":[{"type":1,"name":"{{{acSub}}}","options":[{"name":"title","value":"Blade","focused":true}]}]}}
                    """)!.AsObject());
                Assert.Equal(8, ac["type"]!.GetValue<int>());
                Assert.Contains("Blade Runner 2049", ac.ToJsonString());
            }

            var runAc = await engine.HandleDiscordInteractionAsync(JsonNode.Parse("""
                {"type":4,"member":{"user":{"id":"201","username":"homelab"}},"data":{"name":"cleanerr","options":[{"type":1,"name":"run","options":[{"name":"workflow","value":"Slash","focused":true}]}]}}
                """)!.AsObject());
            Assert.Equal(8, runAc["type"]!.GetValue<int>());
            Assert.Contains("Slash Run Workflow", runAc.ToJsonString());

            // 3. All 7 slash commands (type: 2)
            var statusRes = await engine.HandleDiscordInteractionAsync(JsonNode.Parse("""
                {"type":2,"member":{"user":{"id":"201","username":"homelab"}},"data":{"name":"cleanerr","options":[{"type":1,"name":"status"}]}}
                """)!.AsObject());
            Assert.Equal(4, statusRes["type"]!.GetValue<int>());
            Assert.Contains("Lifecycle Status", statusRes.ToJsonString());

            var leavingRes = await engine.HandleDiscordInteractionAsync(JsonNode.Parse("""
                {"type":2,"member":{"user":{"id":"201","username":"homelab"}},"data":{"name":"cleanerr","options":[{"type":1,"name":"leaving-soon"}]}}
                """)!.AsObject());
            Assert.Equal(4, leavingRes["type"]!.GetValue<int>());
            Assert.Contains("Blade Runner 2049", leavingRes.ToJsonString());

            var whyRes = await engine.HandleDiscordInteractionAsync(JsonNode.Parse("""
                {"type":2,"member":{"user":{"id":"201","username":"homelab"}},"data":{"name":"cleanerr","options":[{"type":1,"name":"why","options":[{"name":"title","value":"Blade Runner 2049"}]}]}}
                """)!.AsObject());
            Assert.Equal(4, whyRes["type"]!.GetValue<int>());
            Assert.Contains("Test Media Explainer", whyRes.ToJsonString());

            var postponeRes = await engine.HandleDiscordInteractionAsync(JsonNode.Parse("""
                {"type":2,"member":{"user":{"id":"201","username":"homelab"}},"data":{"name":"cleanerr","options":[{"type":1,"name":"postpone","options":[{"name":"title","value":"Blade Runner 2049"},{"name":"days","value":14}]}]}}
                """)!.AsObject());
            Assert.Equal(4, postponeRes["type"]!.GetValue<int>());
            Assert.Contains("Postponed +14 Days", postponeRes["data"]!["content"]!.GetValue<string>());

            var excludeRes = await engine.HandleDiscordInteractionAsync(JsonNode.Parse("""
                {"type":2,"member":{"user":{"id":"201","username":"homelab"}},"data":{"name":"cleanerr","options":[{"type":1,"name":"exclude","options":[{"name":"title","value":"Dune: Part Two"},{"name":"duration","value":"30d"}]}]}}
                """)!.AsObject());
            Assert.Equal(4, excludeRes["type"]!.GetValue<int>());
            Assert.Contains("Excluded for 30d", excludeRes.ToJsonString());

            var exclusionsRes = await engine.HandleDiscordInteractionAsync(JsonNode.Parse("""
                {"type":2,"member":{"user":{"id":"201","username":"homelab"}},"data":{"name":"cleanerr","options":[{"type":1,"name":"exclusions"}]}}
                """)!.AsObject());
            Assert.Equal(4, exclusionsRes["type"]!.GetValue<int>());
            Assert.Contains("Active Cleanerr Exclusions", exclusionsRes.ToJsonString());
            Assert.Contains("clnr_del_excl", exclusionsRes.ToJsonString());

            var runRes = await engine.HandleDiscordInteractionAsync(JsonNode.Parse($$$"""
                {"type":2,"member":{"user":{"id":"201","username":"homelab"}},"data":{"name":"cleanerr","options":[{"type":1,"name":"run","options":[{"name":"workflow","value":"id:{{{wf.Id}}}"},{"name":"dry_run","value":true}]}]}}
                """)!.AsObject());
            Assert.Equal(4, runRes["type"]!.GetValue<int>());
            Assert.Contains("Cleanerr Workflow Executed", runRes.ToJsonString());
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task DiscordAuthModes_ChannelMembers_DiscordRoles_AndLinkedPlexRbacQuota()
    {
        var tempDir = CreateTempDir();
        try
        {
            await using var factory = CreateWebFactory(tempDir);
            using var scope = factory.Services.CreateScope();

            var discord = scope.ServiceProvider.GetRequiredService<ICleanerrDiscordService>();
            var engine = scope.ServiceProvider.GetRequiredService<ICleanerrLifecycleEngine>();
            var users = scope.ServiceProvider.GetRequiredService<IPlexUserService>();
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();

            var m1 = await engine.UpsertMediaItemAsync(new UpsertCleanerrMediaItemRequest("rk-auth-1", "movie", "Movie Alpha", Year: 2021, SizeBytes: 5_000_000_000));
            var m2 = await engine.UpsertMediaItemAsync(new UpsertCleanerrMediaItemRequest("rk-auth-2", "movie", "Movie Beta", Year: 2022, SizeBytes: 6_000_000_000));
            var m3 = await engine.UpsertMediaItemAsync(new UpsertCleanerrMediaItemRequest("rk-auth-3", "movie", "Movie Gamma", Year: 2023, SizeBytes: 7_000_000_000));
            var m4 = await engine.UpsertMediaItemAsync(new UpsertCleanerrMediaItemRequest("rk-auth-4", "movie", "Movie Delta", Year: 2024, SizeBytes: 8_000_000_000));

            // 1. channel_members (default) allows any channel member
            var defaultCfg = await discord.GetSettingsAsync();
            Assert.Equal("channel_members", defaultCfg.AuthMode);

            var channelMemberOk = await engine.HandleDiscordInteractionAsync(JsonNode.Parse($$$"""
                {"type":3,"member":{"user":{"id":"3001","username":"anyuser"}},"data":{"custom_id":"clnr_excl:{{{m1.Id}}}"}}
                """)!.AsObject());
            Assert.Contains("Excluded Permanently", channelMemberOk.ToJsonString());

            // 2. discord_roles mode
            await discord.UpdateSettingsAsync(new UpdateCleanerrDiscordSettingsRequest(
                GatewayPassiveShadowMode: true,
                AuthMode: "discord_roles",
                AllowedRoleIds: ["role-curator-99"]));

            var roleDenied = await engine.HandleDiscordInteractionAsync(JsonNode.Parse($$$"""
                {"type":3,"member":{"roles":["role-guest"],"user":{"id":"3002","username":"guest"}},"data":{"custom_id":"clnr_excl:{{{m2.Id}}}"}}
                """)!.AsObject());
            Assert.Contains("Permission Denied", roleDenied.ToJsonString());

            var roleAllowed = await engine.HandleDiscordInteractionAsync(JsonNode.Parse($$$"""
                {"type":3,"member":{"roles":["role-curator-99"],"user":{"id":"3002","username":"curator"}},"data":{"custom_id":"clnr_excl:{{{m2.Id}}}"}}
                """)!.AsObject());
            Assert.Contains("Excluded Permanently", roleAllowed.ToJsonString());

            // 3. linked_plex_rbac mode (RBAC scopes + CleanerrExclusionLimit quota)
            await discord.UpdateSettingsAsync(new UpdateCleanerrDiscordSettingsRequest(
                AuthMode: "linked_plex_rbac"));

            var unlinkedDenied = await engine.HandleDiscordInteractionAsync(JsonNode.Parse($$$"""
                {"type":3,"member":{"user":{"id":"999999","username":"unlinked"}},"data":{"custom_id":"clnr_excl:{{{m3.Id}}}"}}
                """)!.AsObject());
            Assert.Contains("Permission Denied", unlinkedDenied.ToJsonString());

            var quotaRole = await roles.CreateCustomRoleAsync(new UpsertCustomRoleRequest(
                Name: "Cleanerr Quota 1",
                Key: "cleanerr:quota1",
                Description: "Allow reading and up to 1 exclusion",
                Color: "#38bdf8",
                Scopes: [PermissionScopes.CleanerrRead, PermissionScopes.CleanerrExclude],
                CleanerrExclusionLimit: 1));

            // Create server owner first so second Plex user is non-owner and receives custom role cleanly
            await users.UpsertFromPlexAsync(new UpsertPlexUserCommand(
                1001, "owner-uid", "plexowner", "owner@plex.local", null, true, true, true, ["1"]));
            var limitedUser = await users.UpsertFromPlexAsync(new UpsertPlexUserCommand(
                1002, "limited-uid", "plexlimited", "limited@plex.local", null, false, false, true, ["1"]));
            await users.AssignRolesAsync(limitedUser.Id, [quotaRole.Id]);
            await users.UpdatePreferencesAndLinksAsync(limitedUser.Id, null, null, "777666555", "limiteddiscord");

            // First exclusion succeeds within quota=1
            var quotaFirstOk = await engine.HandleDiscordInteractionAsync(JsonNode.Parse($$$"""
                {"type":3,"member":{"user":{"id":"777666555","username":"limiteddiscord"}},"data":{"custom_id":"clnr_excl:{{{m3.Id}}}"}}
                """)!.AsObject());
            Assert.Contains("Excluded Permanently", quotaFirstOk.ToJsonString());

            // Second exclusion exceeds quota=1
            var quotaSecondBlocked = await engine.HandleDiscordInteractionAsync(JsonNode.Parse($$$"""
                {"type":3,"member":{"user":{"id":"777666555","username":"limiteddiscord"}},"data":{"custom_id":"clnr_excl:{{{m4.Id}}}"}}
                """)!.AsObject());
            Assert.Contains("Exclusion Quota Reached", quotaSecondBlocked.ToJsonString());
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task DiscordDiscovery_CommandSync_TestNotification_ReconnectEndpoint_AndZeroHardcodedSecrets()
    {
        // 1. Zero hardcoded secrets on static defaults & fresh settings
        Assert.Equal("", CleanerrCrypto.DefaultDiscordAppId);
        Assert.Equal("", CleanerrCrypto.DefaultDiscordPublicKeyHex);

        var http = new RecordingHttp
        {
            Responder = (req, _) =>
            {
                var path = req.RequestUri!.AbsolutePath;
                if (req.Method == HttpMethod.Get && path == "/api/v10/users/@me")
                {
                    return RecordingHttp.Json("""{"id":"bot-999","username":"CleanerrTestBot"}""");
                }

                if (req.Method == HttpMethod.Get && path == "/api/v10/users/@me/guilds")
                {
                    return RecordingHttp.Json("""[{"id":"guild-1","name":"Homelab Guild","owner":true}]""");
                }

                if (req.Method == HttpMethod.Get && path == "/api/v10/guilds/guild-1/channels")
                {
                    return RecordingHttp.Json("""
                        [
                          {"id":"ch-cat","name":"MEDIA","type":4,"position":0},
                          {"id":"ch-voice","name":"Voice","type":2,"position":1},
                          {"id":"ch-news","name":"announcements","type":5,"position":2},
                          {"id":"ch-text","name":"cleanerr-alerts","type":0,"position":1}
                        ]
                        """);
                }

                if (req.Method == HttpMethod.Put && path == "/api/v10/applications/app-123/guilds/guild-1/commands")
                {
                    return RecordingHttp.Json("""[{"id":"cmd-1","name":"cleanerr"}]""");
                }

                if (req.Method == HttpMethod.Post && path == "/api/v10/channels/ch-text/messages")
                {
                    return RecordingHttp.Json("""{"id":"msg-987654","channel_id":"ch-text"}""");
                }

                return RecordingHttp.Json("{}", HttpStatusCode.NotFound);
            },
        };

        await using var host = await HostTestHarness.StartAsync(http);
        var discord = host.Get<ICleanerrDiscordService>();

        var freshDefaults = await discord.GetSettingsAsync();
        Assert.Equal("", freshDefaults.ApplicationId);
        Assert.Equal("", freshDefaults.PublicKeyHex);
        Assert.Equal("", freshDefaults.GuildId);
        Assert.Equal("", freshDefaults.ChannelId);
        Assert.False(freshDefaults.HasBotToken);
        Assert.Equal("channel_members", freshDefaults.AuthMode);

        // Configure test bot settings (passive mode prevents real WebSocket connection)
        await discord.UpdateSettingsAsync(new UpdateCleanerrDiscordSettingsRequest(
            BotToken: "test-bot-token.segment2.segment3",
            ApplicationId: "app-123",
            GuildId: "guild-1",
            ChannelId: "ch-text",
            PublicBaseUrl: "https://managerr.lan",
            GatewayPassiveShadowMode: true));

        // 2. DiscoverGuildsAndChannelsAsync (filters to type 0 | 5)
        var discovery = await discord.DiscoverGuildsAndChannelsAsync();
        Assert.True(discovery.Ok);
        Assert.Equal("bot-999", discovery.BotUserId);
        Assert.Equal("CleanerrTestBot", discovery.BotUsername);
        Assert.Single(discovery.Guilds);
        Assert.Equal(2, discovery.Channels.Count);
        Assert.All(discovery.Channels, c => Assert.Contains(c.Type, new[] { 0, 5 }));
        Assert.Equal("ch-text", discovery.Channels[0].Id);
        Assert.Equal("ch-news", discovery.Channels[1].Id);

        // 3. SyncSlashCommandsAsync
        var syncResult = await discord.SyncSlashCommandsAsync();
        Assert.True(syncResult.Ok);
        Assert.Equal("guild:guild-1", syncResult.Scope);
        Assert.Equal(7, syncResult.CommandsRegistered);

        // 4. SendTestNotificationAsync (must include interactive components)
        var testNotify = await discord.SendTestNotificationAsync();
        Assert.True(testNotify["sent"]!.GetValue<bool>());
        Assert.Equal("bot_api", testNotify["delivery_mode"]!.GetValue<string>());
        Assert.Equal("msg-987654", testNotify["message_id"]!.GetValue<string>());

        var msgReq = Assert.Single(http.Requests, r => r.Method == "POST" && r.Uri.AbsolutePath == "/api/v10/channels/ch-text/messages");
        var sentPayload = JsonNode.Parse(msgReq.Body)!.AsObject();
        Assert.NotNull(sentPayload["components"]);
        Assert.Contains("clnr_test_sel", msgReq.Body);
        Assert.Contains("clnr_test_btn", msgReq.Body);

        // 5. POST /api/v1/cleanerr/discord/reconnect endpoint (authenticated)
        var tempDir = CreateTempDir();
        try
        {
            const string adminPass = "ReconnectAdmin!2026";
            await using var factory = CreateAuthenticatedWebFactory(tempDir, adminPass, http);
            using var client = await CreateAuthenticatedClientAsync(factory, adminPass);

            using var scope = factory.Services.CreateScope();
            var scopedDiscord = scope.ServiceProvider.GetRequiredService<ICleanerrDiscordService>();
            var reconnectSignalFired = false;
            scopedDiscord.ConfigChanged += () => reconnectSignalFired = true;

            var resp = await client.PostAsync("/api/v1/cleanerr/discord/reconnect", null);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync())!.AsObject();
            Assert.True(json["reconnecting"]!.GetValue<bool>());
            Assert.True(reconnectSignalFired);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task DiscordAuthAudit_ScopeSeparation_AutocompleteAuth_InactiveUser_ChannelMatch_AndShowTitleFormat()
    {
        var tempDir = CreateTempDir();
        try
        {
            await using var factory = CreateWebFactory(tempDir);
            using var scope = factory.Services.CreateScope();

            var discord = scope.ServiceProvider.GetRequiredService<ICleanerrDiscordService>();
            var engine = scope.ServiceProvider.GetRequiredService<ICleanerrLifecycleEngine>();
            var users = scope.ServiceProvider.GetRequiredService<IPlexUserService>();
            var roles = scope.ServiceProvider.GetRequiredService<IRoleService>();

            var show = await engine.UpsertMediaItemAsync(new UpsertCleanerrMediaItemRequest(
                "rk-da-show", "show", "Andor", Year: 2022, SizeBytes: 12_000_000_000));
            var movie = await engine.UpsertMediaItemAsync(new UpsertCleanerrMediaItemRequest(
                "rk-da-movie", "movie", "Dune", Year: 2021, SizeBytes: 16_000_000_000));
            Assert.Equal("Andor", CleanerrDiscordService.FormatSeriesOrMovieTitle(show));
            Assert.Equal("Dune (2021)", CleanerrDiscordService.FormatSeriesOrMovieTitle(movie));

            await engine.ManualStageItemsAsync(new ManualStageCleanerrItemsRequest([show.Id], GraceDays: 7));
            var wf = await engine.UpsertWorkflowAsync(new UpsertCleanerrWorkflowRequest("Secret Audit WF", Enabled: true, DryRun: true));

            // 1. channel_members with ChannelId denies missing or mismatched channel_id
            await discord.UpdateSettingsAsync(new UpdateCleanerrDiscordSettingsRequest(
                GatewayPassiveShadowMode: true,
                AuthMode: "channel_members",
                ChannelId: "ch-cleanerr-only"));

            var missingCh = await engine.HandleDiscordInteractionAsync(JsonNode.Parse("""
                {"type":2,"member":{"user":{"id":"501","username":"chuser"}},"data":{"name":"cleanerr","options":[{"type":1,"name":"status"}]}}
                """)!.AsObject());
            Assert.Contains("Permission Denied", missingCh.ToJsonString());

            var wrongCh = await engine.HandleDiscordInteractionAsync(JsonNode.Parse("""
                {"type":2,"channel_id":"ch-other","member":{"user":{"id":"501","username":"chuser"}},"data":{"name":"cleanerr","options":[{"type":1,"name":"status"}]}}
                """)!.AsObject());
            Assert.Contains("Permission Denied", wrongCh.ToJsonString());

            var rightCh = await engine.HandleDiscordInteractionAsync(JsonNode.Parse("""
                {"type":2,"channel_id":"ch-cleanerr-only","member":{"user":{"id":"501","username":"chuser"}},"data":{"name":"cleanerr","options":[{"type":1,"name":"status"}]}}
                """)!.AsObject());
            Assert.Contains("Lifecycle Status", rightCh.ToJsonString());

            // 2. linked_plex_rbac: strict scope separation, autocomplete auth, and inactive user rejection
            await discord.UpdateSettingsAsync(new UpdateCleanerrDiscordSettingsRequest(
                GatewayPassiveShadowMode: true,
                AuthMode: "linked_plex_rbac",
                ChannelId: ""));

            // Unauthorized autocomplete returns empty choices (does not leak catalog or workflow titles)
            var unauthAcMedia = await engine.HandleDiscordInteractionAsync(JsonNode.Parse("""
                {"type":4,"member":{"user":{"id":"90001","username":"stranger"}},"data":{"name":"cleanerr","options":[{"type":1,"name":"why","options":[{"name":"title","value":"And","focused":true}]}]}}
                """)!.AsObject());
            Assert.Equal(8, unauthAcMedia["type"]!.GetValue<int>());
            Assert.Empty(unauthAcMedia["data"]!["choices"]!.AsArray());

            var unauthAcWf = await engine.HandleDiscordInteractionAsync(JsonNode.Parse("""
                {"type":4,"member":{"user":{"id":"90001","username":"stranger"}},"data":{"name":"cleanerr","options":[{"type":1,"name":"run","options":[{"name":"workflow","value":"Secret","focused":true}]}]}}
                """)!.AsObject());
            Assert.Equal(8, unauthAcWf["type"]!.GetValue<int>());
            Assert.Empty(unauthAcWf["data"]!["choices"]!.AsArray());

            // Seed owner first so subsequent users are non-owners
            await users.UpsertFromPlexAsync(new UpsertPlexUserCommand(
                2001, "owner-da", "daowner", "daowner@plex.local", null, true, true, true, ["1"]));

            var exclOnlyRole = await roles.CreateCustomRoleAsync(new UpsertCustomRoleRequest(
                "DA Exclude Only", "custom:da-excl", "", "#a855f7",
                [PermissionScopes.CleanerrRead, PermissionScopes.CleanerrExclude]));
            var rescueOnlyRole = await roles.CreateCustomRoleAsync(new UpsertCustomRoleRequest(
                "DA Rescue Only", "custom:da-rescue", "", "#22c55e",
                [PermissionScopes.CleanerrRead, PermissionScopes.CleanerrRescue]));
            var writeOnlyRole = await roles.CreateCustomRoleAsync(new UpsertCustomRoleRequest(
                "DA Write Only", "custom:da-write", "", "#f59e0b",
                [PermissionScopes.CleanerrRead, PermissionScopes.CleanerrWrite]));
            var execOnlyRole = await roles.CreateCustomRoleAsync(new UpsertCustomRoleRequest(
                "DA Exec Only", "custom:da-exec", "", "#ef4444",
                [PermissionScopes.CleanerrRead, PermissionScopes.CleanerrExecute]));

            var exclUser = await users.UpsertFromPlexAsync(new UpsertPlexUserCommand(2002, "excl-da", "daexcl", null, null, false, false, true, ["1"]));
            await users.AssignRolesAsync(exclUser.Id, [exclOnlyRole.Id]);
            await users.UpdatePreferencesAndLinksAsync(exclUser.Id, null, null, "810001", "excldiscord");

            var rescueUser = await users.UpsertFromPlexAsync(new UpsertPlexUserCommand(2003, "res-da", "darescue", null, null, false, false, true, ["1"]));
            await users.AssignRolesAsync(rescueUser.Id, [rescueOnlyRole.Id]);
            await users.UpdatePreferencesAndLinksAsync(rescueUser.Id, null, null, "810002", "rescuediscord");

            var writeUser = await users.UpsertFromPlexAsync(new UpsertPlexUserCommand(2004, "wr-da", "dawrite", null, null, false, false, true, ["1"]));
            await users.AssignRolesAsync(writeUser.Id, [writeOnlyRole.Id]);
            await users.UpdatePreferencesAndLinksAsync(writeUser.Id, null, null, "810003", "writediscord");

            var execUser = await users.UpsertFromPlexAsync(new UpsertPlexUserCommand(2005, "ex-da", "daexec", null, null, false, false, true, ["1"]));
            await users.AssignRolesAsync(execUser.Id, [execOnlyRole.Id]);
            await users.UpdatePreferencesAndLinksAsync(execUser.Id, null, null, "810004", "execdiscord");

            var inactiveUser = await users.UpsertFromPlexAsync(new UpsertPlexUserCommand(2006, "inact-da", "dainactive", null, null, false, false, false, ["1"]));
            await users.AssignRolesAsync(inactiveUser.Id, [exclOnlyRole.Id]);
            await users.UpdatePreferencesAndLinksAsync(inactiveUser.Id, null, null, "810005", "inactivediscord");

            // Authorized reader gets autocomplete choices
            var authAc = await engine.HandleDiscordInteractionAsync(JsonNode.Parse("""
                {"type":4,"member":{"user":{"id":"810001","username":"excldiscord"}},"data":{"name":"cleanerr","options":[{"type":1,"name":"why","options":[{"name":"title","value":"Andor","focused":true}]}]}}
                """)!.AsObject());
            Assert.Equal(8, authAc["type"]!.GetValue<int>());
            Assert.Contains("Andor", authAc.ToJsonString());

            // Inactive user rejected even with CleanerrExclude + CleanerrRead role
            var inactiveDenied = await engine.HandleDiscordInteractionAsync(JsonNode.Parse("""
                {"type":2,"member":{"user":{"id":"810005","username":"inactivediscord"}},"data":{"name":"cleanerr","options":[{"type":1,"name":"status"}]}}
                """)!.AsObject());
            Assert.Contains("Permission Denied", inactiveDenied.ToJsonString());

            // Scope separation checks:
            // - Exclude-only user CANNOT postpone (requires CleanerrRescue), CANNOT run (requires CleanerrExecute), CANNOT delete exclusion (requires CleanerrWrite)
            var exclCannotPostpone = await engine.HandleDiscordInteractionAsync(JsonNode.Parse($$$"""
                {"type":3,"member":{"user":{"id":"810001","username":"excldiscord"}},"data":{"custom_id":"clnr_postpone:{{{show.Id}}}:7"}}
                """)!.AsObject());
            Assert.Contains("Permission Denied", exclCannotPostpone.ToJsonString());
            Assert.Contains(PermissionScopes.CleanerrRescue, exclCannotPostpone.ToJsonString());

            var exclCannotRun = await engine.HandleDiscordInteractionAsync(JsonNode.Parse($$$"""
                {"type":2,"member":{"user":{"id":"810001","username":"excldiscord"}},"data":{"name":"cleanerr","options":[{"type":1,"name":"run","options":[{"name":"workflow","value":"id:{{{wf.Id}}}"}]}]}}
                """)!.AsObject());
            Assert.Contains("Permission Denied", exclCannotRun.ToJsonString());
            Assert.Contains(PermissionScopes.CleanerrExecute, exclCannotRun.ToJsonString());

            // Rescue user CAN postpone, CANNOT exclude
            var rescueCanPostpone = await engine.HandleDiscordInteractionAsync(JsonNode.Parse($$$"""
                {"type":3,"member":{"user":{"id":"810002","username":"rescuediscord"}},"data":{"custom_id":"clnr_postpone:{{{show.Id}}}:7"}}
                """)!.AsObject());
            Assert.Contains("Postponed +7d", rescueCanPostpone["data"]!["content"]!.GetValue<string>());

            // Exclude user CAN exclude, then CANNOT delete exclusion rule (requires CleanerrWrite)
            var exclCanExclude = await engine.HandleDiscordInteractionAsync(JsonNode.Parse($$$"""
                {"type":3,"member":{"user":{"id":"810001","username":"excldiscord"}},"data":{"custom_id":"clnr_excl:{{{show.Id}}}"}}
                """)!.AsObject());
            Assert.Contains("Excluded Permanently", exclCanExclude.ToJsonString());

            var ruleId = (await engine.ListExclusionRulesAsync()).First().Id;
            var exclCannotDelRule = await engine.HandleDiscordInteractionAsync(JsonNode.Parse($$$"""
                {"type":3,"member":{"user":{"id":"810001","username":"excldiscord"}},"data":{"custom_id":"clnr_del_excl","values":["{{{ruleId}}}"]}}
                """)!.AsObject());
            Assert.Contains("Permission Denied", exclCannotDelRule.ToJsonString());
            Assert.Contains(PermissionScopes.CleanerrWrite, exclCannotDelRule.ToJsonString());

            // Write user CAN delete exclusion rule
            var writeCanDelRule = await engine.HandleDiscordInteractionAsync(JsonNode.Parse($$$"""
                {"type":3,"member":{"user":{"id":"810003","username":"writediscord"}},"data":{"custom_id":"clnr_del_excl","values":["{{{ruleId}}}"]}}
                """)!.AsObject());
            Assert.Contains("Revoked 1 Cleanerr exclusion rule", writeCanDelRule.ToJsonString());

            // Execute user CAN /cleanerr run
            var execCanRun = await engine.HandleDiscordInteractionAsync(JsonNode.Parse($$$"""
                {"type":2,"member":{"user":{"id":"810004","username":"execdiscord"}},"data":{"name":"cleanerr","options":[{"type":1,"name":"run","options":[{"name":"workflow","value":"id:{{{wf.Id}}}"},{"name":"dry_run","value":true}]}]}}
                """)!.AsObject());
            Assert.Contains("Cleanerr Workflow Executed", execCanRun.ToJsonString());
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    private static WebApplicationFactory<Program> CreateWebFactory(string tempDir) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Managerr:ConfigDirectory"] = tempDir,
                ["Managerr:MasterKey"] = "phase5-discord-security-key-32b!",
                ["Managerr:SchedulerEnabled"] = "false",
            }));
        });

    private static WebApplicationFactory<Program> CreateAuthenticatedWebFactory(string tempDir, string password, RecordingHttp http) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Managerr:ConfigDirectory"] = tempDir,
                ["Managerr:MasterKey"] = "phase5-discord-security-key-32b!",
                ["Managerr:SchedulerEnabled"] = "false",
                ["Managerr:TestbedBootstrap:Enabled"] = "true",
                ["Managerr:TestbedBootstrap:Username"] = "testadmin",
                ["Managerr:TestbedBootstrap:Password"] = password,
            }));
            b.ConfigureServices(s =>
            {
                s.RemoveAll<ISafeHttpClientFactory>();
                s.AddSingleton<ISafeHttpClientFactory>(http);
            });
        });

    private static async Task<HttpClient> CreateAuthenticatedClientAsync(WebApplicationFactory<Program> factory, string password)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var loginResp = await client.PostAsJsonAsync("/api/v1/auth/breakglass/login", new
        {
            usernameOrEmail = "testadmin",
            password,
        });
        loginResp.EnsureSuccessStatusCode();
        var body = await loginResp.Content.ReadFromJsonAsync<JsonElement>();
        if (body.TryGetProperty("csrfToken", out var csrf) && csrf.GetString() is { Length: > 0 } token)
        {
            client.DefaultRequestHeaders.Add("X-Managerr-Csrf", token);
        }

        return client;
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"managerr-p5-discord-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDeleteDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup
        }
    }
}
