using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Dapper;
using Managerr.Common.Crypto;
using Managerr.Common.Net;
using Managerr.Common.Paths;
using Managerr.Datastore;
using Managerr.Datastore.Migrations;
using Managerr.Indexers;
using Managerr.Indexers.Cardigann;
using Managerr.Indexers.Categories;
using Microsoft.Extensions.Options;
using Xunit;

namespace Managerr.Phase2.Tests;

public sealed class RecordingCardigannHttpGateway : IIndexerHttpGateway
{
    public Func<IndexerHttpRequest, IndexerProxyInternal?, IndexerHttpResponse> SendHandler { get; set; } =
        (req, _) => new IndexerHttpResponse(200, "", [], req.TargetUri);

    public List<(IndexerHttpRequest Request, IndexerProxyInternal? Proxy)> SentRequests { get; } = [];

    public async Task<string> FetchStringAsync(Uri targetUri, IndexerProxyInternal? matchedProxy, CancellationToken ct = default)
    {
        var resp = await SendAsync(new IndexerHttpRequest(targetUri), matchedProxy, ct);
        return resp.Body;
    }

    public Task<IndexerHttpResponse> SendAsync(
        IndexerHttpRequest request,
        IndexerProxyInternal? matchedProxy,
        CancellationToken ct = default)
    {
        lock (SentRequests)
        {
            SentRequests.Add((request, matchedProxy));
        }
        return Task.FromResult(SendHandler(request, matchedProxy));
    }
}

public sealed class CardigannEngineParityTests : IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"managerr-cardigann-{Guid.NewGuid():N}");
    private AppPaths _paths = null!;
    private SqliteDbFactory _dbFactory = null!;
    private EnvelopeSecretProtector _protector = null!;

    public async Task InitializeAsync()
    {
        _paths = new AppPaths(_tempDir);
        _dbFactory = new SqliteDbFactory(_paths);
        _protector = new EnvelopeSecretProtector(_paths, "cardigann-v12-master-key");
        var migrator = new SchemaMigrator(_dbFactory);
        await migrator.MigrateAllAsync();
    }

    public async Task DisposeAsync()
    {
        await _dbFactory.DisposeAsync();
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* ignore */ }
        }
    }

    // -------------------------------------------------------------------------
    // Fixture 1: Private tracker with login: method: form
    // (hidden CSRF token + selectorinputs + session cookie reuse + auto re-auth on expiry)
    // -------------------------------------------------------------------------
    [Fact]
    public async Task Fixture1_PrivateFormLoginTracker_ExtractsCsrfAndSelectorInputs_ReusesSessionCookies_AndReauthenticatesOnExpiry()
    {
        const string yaml = """
            id: private-form-tracker
            name: NexusPrivate HD
            type: private
            language: en-US
            links:
              - https://nexus.private.lan/
            caps:
              categorymappings:
                - {id: 401, cat: Movies/UHD, desc: "Movies 2160p"}
                - {id: 405, cat: TV/HD, desc: "TV 1080p"}
            settings:
              - name: username
                type: text
                label: Username
              - name: password
                type: password
                label: Password
              - name: freeleech_only
                type: checkbox
                label: Freeleech Only
                default: false
            login:
              path: login.php
              method: form
              form: form#login-box
              inputs:
                username: "{{ .Config.username }}"
                password: "{{ .Config.password }}"
              selectorinputs:
                st_token:
                  selector: meta[name="st-token"]
                  attribute: content
              error:
                - selector: div.alert-danger
              test:
                path: usercp.php
                selector: a[href*="logout.php"]
            search:
              paths:
                - path: torrents.php
              inputs:
                search: "{{ .Keywords }}"
                free: "{{ if .Config.freeleech_only }}1{{ else }}0{{ end }}"
              rows:
                selector: table.torrents > tbody > tr
              fields:
                title:
                  selector: td.name a.torrent-title
                details:
                  selector: td.name a.torrent-title
                  attribute: href
                download:
                  selector: td.dl a
                  attribute: href
                size:
                  selector: td.size
                seeders:
                  selector: td.seeders
                leechers:
                  selector: td.leechers
                category:
                  selector: td.cat
                  attribute: data-cat-id
            """;

        await File.WriteAllTextAsync(Path.Combine(_paths.CustomDefinitionsDirectory, "private-form-tracker.yml"), yaml);
        var catalog = new CardigannDefinitionCatalog(_paths);
        var gateway = new RecordingCardigannHttpGateway();
        var service = new UnifiedIndexerService(_dbFactory, _protector, catalog, gateway);

        var loginGetCount = 0;
        var loginPostCount = 0;
        var activeSessionId = "sess_valid_v1";
        var simulateExpiredOnce = false;

        gateway.SendHandler = (req, _) =>
        {
            var path = req.TargetUri.AbsolutePath;
            if (path == "/login.php" && req.Method == "GET")
            {
                loginGetCount++;
                return new IndexerHttpResponse(
                    200,
                    """
                    <html>
                      <head><meta name="st-token" content="sec_tok_8842" /></head>
                      <body>
                        <form id="login-box" action="/takelogin.php" method="post">
                          <input type="hidden" name="csrf_token" value="csrf_hidden_9911" />
                          <input type="hidden" name="returnto" value="/torrents.php" />
                          <input type="text" name="username" value="" />
                          <input type="password" name="password" value="" />
                          <input type="checkbox" name="remember_me" value="yes" checked />
                          <input type="checkbox" name="unused_opt" value="no" />
                          <input type="submit" name="submit" value="Log in" />
                        </form>
                      </body>
                    </html>
                    """,
                    ["prelogin_cookie=pre123; Path=/; HttpOnly"],
                    req.TargetUri);
            }

            if (path == "/takelogin.php" && req.Method == "POST")
            {
                loginPostCount++;
                Assert.Contains("prelogin_cookie=pre123", req.CookieHeader ?? "");
                var fields = (req.FormFields ?? []).ToDictionary(k => k.Key, v => v.Value);
                Assert.Equal("csrf_hidden_9911", fields["csrf_token"]);
                Assert.Equal("/torrents.php", fields["returnto"]);
                Assert.Equal("yes", fields["remember_me"]);
                Assert.False(fields.ContainsKey("unused_opt"));
                Assert.Equal("sec_tok_8842", fields["st_token"]);
                Assert.Equal("trackeruser", fields["username"]);
                Assert.Equal("hunter2-secret", fields["password"]);

                return new IndexerHttpResponse(
                    200,
                    """<html><body>Welcome trackeruser! <a href="/logout.php?id=1">Logout</a></body></html>""",
                    [$"nexus_sid={activeSessionId}; Path=/; HttpOnly", "uid=77; Path=/"],
                    new Uri("https://nexus.private.lan/index.php"));
            }

            if (path == "/usercp.php")
            {
                Assert.Contains($"nexus_sid={activeSessionId}", req.CookieHeader ?? "");
                Assert.Contains("uid=77", req.CookieHeader ?? "");
                return new IndexerHttpResponse(
                    200,
                    """<html><body><div class="nav"><a href="/logout.php">Log out</a></div></body></html>""",
                    [],
                    req.TargetUri);
            }

            if (path == "/torrents.php")
            {
                if (simulateExpiredOnce)
                {
                    simulateExpiredOnce = false;
                    activeSessionId = "sess_refreshed_v2";
                    // Simulate expired cookie redirecting/rendering login form with 0 rows
                    return new IndexerHttpResponse(
                        200,
                        """<html><body><form id="login-box" action="/takelogin.php"><input name="username" /></form></body></html>""",
                        [],
                        new Uri("https://nexus.private.lan/login.php"));
                }

                Assert.Contains($"nexus_sid={activeSessionId}", req.CookieHeader ?? "");
                Assert.Contains("free=0", req.TargetUri.Query);
                return new IndexerHttpResponse(
                    200,
                    """
                    <html><body>
                      <table class="torrents"><tbody>
                        <tr>
                          <td class="cat" data-cat-id="401">UHD</td>
                          <td class="name"><a class="torrent-title" href="/details.php?id=501">Blade.Runner.2049.2160p.UHD.BluRay.REMUX-GRP</a></td>
                          <td class="dl"><a href="/download.php?id=501">DL</a></td>
                          <td class="size">58.4 GiB</td>
                          <td class="seeders">84</td>
                          <td class="leechers">6</td>
                        </tr>
                      </tbody></table>
                    </body></html>
                    """,
                    [],
                    req.TargetUri);
            }

            return new IndexerHttpResponse(404, "Not found", [], req.TargetUri);
        };

        var created = await service.CreateIndexerAsync(new UpsertIndexerRequest(
            Name: "NexusPrivate",
            Implementation: "cardigann",
            DefinitionId: "private-form-tracker",
            Protocol: "torrent",
            BaseUrl: "https://nexus.private.lan",
            ExtraSettings: new Dictionary<string, string>
            {
                ["username"] = "trackeruser",
                ["password"] = "hunter2-secret",
            },
            RequestDelayMs: 0));

        // Verify password is encrypted at rest in SQLite extra_settings_json
        await using (var conn = await _dbFactory.OpenReadConnectionAsync(DatabaseKind.Core))
        {
            var rawExtra = await conn.ExecuteScalarAsync<string>(
                "SELECT extra_settings_json FROM indexers WHERE id = @Id;",
                new { created.Id });
            Assert.NotNull(rawExtra);
            Assert.DoesNotContain("hunter2-secret", rawExtra);
        }

        // Search 1: triggers full form login + usercp test + torrents.php search
        var firstResults = await service.SearchAsync(new IndexerSearchQuery(
            Query: "Blade Runner 2049",
            MediaVertical: MediaVerticals.Movies));

        var rel = Assert.Single(firstResults);
        Assert.Equal("Blade.Runner.2049.2160p.UHD.BluRay.REMUX-GRP", rel.Title);
        Assert.Equal("https://nexus.private.lan/download.php?id=501", rel.DownloadUrl);
        Assert.Equal("https://nexus.private.lan/details.php?id=501", rel.InfoUrl);
        Assert.Equal(84, rel.Seeders);
        Assert.Equal(90, rel.Peers);
        Assert.Contains(2045, rel.Categories);
        Assert.Equal(1, loginGetCount);
        Assert.Equal(1, loginPostCount);

        // Search 2: MUST reuse cached session cookie without calling login.php or takelogin.php again!
        var secondResults = await service.SearchAsync(new IndexerSearchQuery(
            Query: "Blade Runner 2049",
            MediaVertical: MediaVerticals.Movies));
        Assert.Single(secondResults);
        Assert.Equal(1, loginGetCount);
        Assert.Equal(1, loginPostCount);

        // Search 3: simulate session expiry -> engine invalidates cookie, re-authenticates once, and succeeds
        simulateExpiredOnce = true;
        var thirdResults = await service.SearchAsync(new IndexerSearchQuery(
            Query: "Blade Runner 2049",
            MediaVertical: MediaVerticals.Movies));
        Assert.Single(thirdResults);
        Assert.Equal(2, loginGetCount);
        Assert.Equal(2, loginPostCount);
    }

    // -------------------------------------------------------------------------
    // Fixture 2: Private tracker with login: method: cookie
    // -------------------------------------------------------------------------
    [Fact]
    public async Task Fixture2_PrivateCookieTracker_AuthenticatesWithCookie_VerifiesTestSelector_AndSearches()
    {
        const string yaml = """
            id: private-cookie-tracker
            name: CookieVault Tracker
            type: private
            links:
              - https://cookievault.private.lan/
            caps:
              categorymappings:
                - {id: 1, cat: TV/Anime, desc: "Anime HD"}
            settings:
              - name: cookie
                type: text
                label: Session Cookie
              - name: useragent
                type: text
                label: Custom User-Agent
                default: "ManagerrCardigann/1.0"
            login:
              method: cookie
              cookies: ["lang=en"]
              headers:
                User-Agent: ["{{ .Config.useragent }}"]
              test:
                path: index.php
                selector: "div#user-bar span.username"
            search:
              paths:
                - path: browse.php
              inputs:
                q: "{{ .Keywords }}"
              rows:
                selector: "div.torrent-row"
              fields:
                title:
                  selector: "a.t-name"
                download:
                  selector: "a.t-dl"
                  attribute: href
                size:
                  selector: "span.t-size"
                seeders:
                  selector: "span.t-seed"
                category:
                  text: "1"
            """;

        await File.WriteAllTextAsync(Path.Combine(_paths.CustomDefinitionsDirectory, "private-cookie-tracker.yml"), yaml);
        var catalog = new CardigannDefinitionCatalog(_paths);
        var gateway = new RecordingCardigannHttpGateway
        {
            SendHandler = (req, _) =>
            {
                Assert.Equal("ManagerrCardigann/1.0", req.Headers?["User-Agent"]);
                var hasValidCookie = (req.CookieHeader ?? "").Contains("cf_clearance=ok123", StringComparison.Ordinal) &&
                                     (req.CookieHeader ?? "").Contains("session_token=vault999", StringComparison.Ordinal) &&
                                     (req.CookieHeader ?? "").Contains("lang=en", StringComparison.Ordinal);

                if (req.TargetUri.AbsolutePath == "/index.php")
                {
                    return hasValidCookie
                        ? new IndexerHttpResponse(200, """<html><body><div id="user-bar"><span class="username">trackeruser</span></div></body></html>""", [], req.TargetUri)
                        : new IndexerHttpResponse(200, """<html><body><a href="/login">Sign In</a></body></html>""", [], req.TargetUri);
                }

                if (req.TargetUri.AbsolutePath == "/browse.php" && hasValidCookie)
                {
                    return new IndexerHttpResponse(
                        200,
                        """
                        <html><body>
                          <div class="torrent-row">
                            <a class="t-name">[SubsPlease] Dandadan - 07 (1080p)</a>
                            <a class="t-dl" href="/dl/777.torrent">Download</a>
                            <span class="t-size">1.25 GiB</span>
                            <span class="t-seed">310</span>
                          </div>
                        </body></html>
                        """,
                        [],
                        req.TargetUri);
                }

                return new IndexerHttpResponse(403, "Forbidden", [], req.TargetUri);
            },
        };

        var service = new UnifiedIndexerService(_dbFactory, _protector, catalog, gateway);

        // Invalid cookie fails TestIndexerAsync because login.test selector is missing
        var badTest = await service.TestIndexerAsync(new UpsertIndexerRequest(
            Name: "CookieVault Bad",
            Implementation: "cardigann",
            DefinitionId: "private-cookie-tracker",
            Protocol: "torrent",
            BaseUrl: "https://cookievault.private.lan",
            ExtraSettings: new Dictionary<string, string> { ["cookie"] = "expired=1" }));
        Assert.False(badTest.Ok);
        Assert.Contains("div#user-bar span.username", badTest.Message);

        // Valid cookie passes TestIndexerAsync and SearchAsync
        var validReq = new UpsertIndexerRequest(
            Name: "CookieVault",
            Implementation: "cardigann",
            DefinitionId: "private-cookie-tracker",
            Protocol: "torrent",
            BaseUrl: "https://cookievault.private.lan",
            ExtraSettings: new Dictionary<string, string>
            {
                ["cookie"] = "cf_clearance=ok123; session_token=vault999",
            },
            RequestDelayMs: 0);

        var okTest = await service.TestIndexerAsync(validReq);
        Assert.True(okTest.Ok, okTest.Message);
        Assert.Contains("1 items parsed", okTest.Message);

        await service.CreateIndexerAsync(validReq);
        var results = await service.SearchAsync(new IndexerSearchQuery(
            Query: "Dandadan",
            MediaVertical: MediaVerticals.Anime));

        var item = Assert.Single(results);
        Assert.Equal("[SubsPlease] Dandadan - 07 (1080p)", item.Title);
        Assert.Equal("https://cookievault.private.lan/dl/777.torrent", item.DownloadUrl);
        Assert.Contains(5070, item.Categories);
    }

    // -------------------------------------------------------------------------
    // Fixture 3: JSON API tracker using {{ if .Query.IMDBID }}, {{ join .Categories "," }},
    // nested rows.selector: data.items, and re_replace / dateparse filters
    // -------------------------------------------------------------------------
    [Fact]
    public async Task Fixture3_JsonApiTracker_EvaluatesImdbConditional_JoinCategories_NestedDotPaths_ReReplace_AndGoDateparse()
    {
        const string yaml = """
            id: json-api-tracker
            name: ApiCinema JSON
            type: private
            links:
              - https://apicinema.lan/
            caps:
              categorymappings:
                - {id: 21, cat: Movies/HD, desc: "Movies 1080p"}
                - {id: 22, cat: Movies/UHD, desc: "Movies 2160p"}
                - {id: 51, cat: TV/HD, desc: "TV 1080p"}
            settings:
              - name: apikey
                type: password
                label: API Token
            search:
              headers:
                Authorization: ["Bearer {{ .Config.apikey }}"]
                Accept: ["application/json"]
              paths:
                - path: "api/v2/torrents?cats={{ join .Categories \",\" }}&{{ if .Query.IMDBID }}imdb={{ .Query.IMDBID }}&imdb_short={{ .Query.IMDBIDShort }}{{ else }}q={{ urlencode .Keywords }}{{ end }}&year={{ .Today.Year }}"
                  response:
                    type: json
              rows:
                selector: data.items
              fields:
                _id:
                  selector: meta.torrent_id
                title:
                  selector: release_name
                  filters:
                    - name: re_replace
                      args: ["^\\[API\\]\\s*", ""]
                    - name: replace
                      args: ["_", "."]
                download:
                  text: "{{ .Config.sitelink }}api/v2/download/{{ .Result._id }}"
                infohash:
                  selector: meta.hash
                size:
                  selector: stats.bytes
                seeders:
                  selector: stats.seeders
                leechers:
                  selector: stats.leechers
                category:
                  selector: category.id
                imdbid:
                  selector: meta.imdb
                tmdbid:
                  selector: meta.tmdb_id
                date:
                  selector: created_at
                  filters:
                    - name: dateparse
                      args: "2006-01-02 15:04:05 -0700"
            """;

        await File.WriteAllTextAsync(Path.Combine(_paths.CustomDefinitionsDirectory, "json-api-tracker.yml"), yaml);
        var catalog = new CardigannDefinitionCatalog(_paths);
        var gateway = new RecordingCardigannHttpGateway
        {
            SendHandler = (req, _) =>
            {
                Assert.Equal("Bearer top-secret-json-token", req.Headers?["Authorization"]);
                Assert.Contains("cats=21,22", Uri.UnescapeDataString(req.TargetUri.Query));
                Assert.Contains("imdb=tt15239678", req.TargetUri.Query);
                Assert.Contains("imdb_short=15239678", req.TargetUri.Query);
                Assert.Contains($"year={DateTimeOffset.UtcNow.Year}", req.TargetUri.Query);

                const string jsonPayload = """
                    {
                      "status": "ok",
                      "data": {
                        "total": 1,
                        "items": [
                          {
                            "release_name": "[API] Dune_Part_Two_2024_2160p_WEB-DL_HEVC-GRP",
                            "created_at": "2026-05-19 20:30:15 +0200",
                            "category": { "id": 22, "name": "Movies UHD" },
                            "meta": {
                              "torrent_id": 98765,
                              "hash": "AABBCCDDEEFF00112233445566778899AABBCCDD",
                              "imdb": "15239678",
                              "tmdb_id": 693134
                            },
                            "stats": {
                              "bytes": 26843545600,
                              "seeders": 245,
                              "leechers": 15
                            }
                          }
                        ]
                      }
                    }
                    """;

                return new IndexerHttpResponse(200, jsonPayload, [], req.TargetUri);
            },
        };

        var service = new UnifiedIndexerService(_dbFactory, _protector, catalog, gateway);
        await service.CreateIndexerAsync(new UpsertIndexerRequest(
            Name: "ApiCinema",
            Implementation: "cardigann",
            DefinitionId: "json-api-tracker",
            Protocol: "torrent",
            BaseUrl: "https://apicinema.lan",
            ApiKey: "top-secret-json-token",
            Categories: [2040, 2045, 5040],
            RequestDelayMs: 0));

        var results = await service.SearchAsync(new IndexerSearchQuery(
            Query: "Dune Part Two",
            MediaVertical: MediaVerticals.Movies,
            Categories: [2040, 2045],
            ImdbId: "15239678"));

        var rel = Assert.Single(results);
        Assert.Equal("Dune.Part.Two.2024.2160p.WEB-DL.HEVC-GRP", rel.Title);
        Assert.Equal("https://apicinema.lan/api/v2/download/98765", rel.DownloadUrl);
        Assert.Equal("aabbccddeeff00112233445566778899aabbccdd", rel.InfoHash);
        Assert.Equal(26843545600L, rel.SizeBytes);
        Assert.Equal(245, rel.Seeders);
        Assert.Equal(260, rel.Peers);
        Assert.Equal("tt15239678", rel.ImdbId);
        Assert.Equal(693134, rel.TmdbId);
        Assert.Equal(new DateTimeOffset(2026, 5, 19, 18, 30, 15, TimeSpan.Zero), rel.PublishedAt);
    }

    // -------------------------------------------------------------------------
    // Fixture 4: Multi-path HTML tracker with keywordsfilters, remove selector,
    // timeago filter, querystring infohash extraction, and downloadvolumefactor case: mapping
    // -------------------------------------------------------------------------
    [Fact]
    public async Task Fixture4_MultiPathHtmlTracker_HandlesKeywordsFilters_RemoveSelector_Timeago_QuerystringHash_FreeleechCase_AndDedup()
    {
        const string yaml = """
            id: multipath-html-tracker
            name: DualPath Public Torrents
            type: public
            links:
              - https://dualpath.example/
            caps:
              categorymappings:
                - {id: mov_uhd, cat: Movies/UHD, desc: "UHD Movies"}
                - {id: mov_hd, cat: Movies/HD, desc: "HD Movies"}
                - {id: tv_hd, cat: TV/HD, desc: "HD TV"}
            search:
              keywordsfilters:
                - name: diacritics
                - name: tolower
                - name: re_replace
                  args: ["[^a-z0-9]+", "-"]
                - name: trim
                  args: "-"
              inputs:
                sort: seeders
              paths:
                - path: "movies/search"
                  categories: [mov_uhd, mov_hd]
                  method: get
                  inputs:
                    slug: "{{ .Keywords }}"
                - path: "movies/featured"
                  categories: [mov_uhd]
                  method: post
                  inheritinputs: false
                  inputs:
                    q: "{{ .Keywords }}"
                    mode: featured
                - path: "tv/search"
                  categories: [tv_hd]
                  method: get
                  inputs:
                    slug: "{{ .Keywords }}"
              rows:
                selector: "table#releases tbody tr"
              fields:
                title:
                  selector: "td.title div.release-line"
                  remove: "span.badge, small.uploader"
                download:
                  selector: "td.links a.torrent-dl"
                  attribute: href
                infohash:
                  selector: "td.links a.torrent-dl"
                  attribute: href
                  filters:
                    - name: querystring
                      args: info_hash
                size:
                  selector: "td.size"
                seeders:
                  selector: "td.seed"
                leechers:
                  selector: "td.leech"
                category:
                  selector: "td.cat"
                date:
                  selector: "td.added"
                  filters:
                    - name: timeago
                downloadvolumefactor:
                  case:
                    "span.badge-freeleech": 0
                    "span.badge-halfleech": 0.5
                    "*": 1
                uploadvolumefactor:
                  case:
                    "span.badge-doubleup": 2
                    "*": 1
            """;

        await File.WriteAllTextAsync(Path.Combine(_paths.CustomDefinitionsDirectory, "multipath-html-tracker.yml"), yaml);
        var catalog = new CardigannDefinitionCatalog(_paths);
        var gateway = new RecordingCardigannHttpGateway
        {
            SendHandler = (req, _) =>
            {
                if (req.TargetUri.AbsolutePath == "/movies/search")
                {
                    Assert.Equal("GET", req.Method);
                    // Inherited sort=seeders + filtered keywords ("Amélie: Director's Cut!" -> "amelie-director-s-cut")
                    Assert.Contains("sort=seeders", req.TargetUri.Query);
                    Assert.Contains("slug=amelie-director-s-cut", req.TargetUri.Query);

                    return new IndexerHttpResponse(
                        200,
                        """
                        <html><body><table id="releases"><tbody>
                          <tr>
                            <td class="cat">mov_uhd</td>
                            <td class="title">
                              <div class="release-line">
                                <span class="badge badge-freeleech">FREELEECH</span>
                                <span class="badge badge-doubleup">2X UP</span>
                                Amelie.2001.2160p.UHD.BluRay.REMUX-GRP
                                <small class="uploader">by SceneBot</small>
                              </div>
                            </td>
                            <td class="links"><a class="torrent-dl" href="/dl.php?info_hash=1111222233334444555566667777888899990000&id=1">DL</a></td>
                            <td class="size">52.0 GiB</td>
                            <td class="seed">120</td>
                            <td class="leech">10</td>
                            <td class="added">2 hours ago</td>
                          </tr>
                          <tr>
                            <td class="cat">mov_hd</td>
                            <td class="title">
                              <div class="release-line">
                                <span class="badge badge-halfleech">50% FREE</span>
                                Amelie.2001.1080p.BluRay.x264-GRP
                              </div>
                            </td>
                            <td class="links"><a class="torrent-dl" href="/dl.php?info_hash=aaaabbbbccccddddeeeeffff0000111122223333&id=2">DL</a></td>
                            <td class="size">12.5 GiB</td>
                            <td class="seed">65</td>
                            <td class="leech">4</td>
                            <td class="added">3 days ago</td>
                          </tr>
                        </tbody></table></body></html>
                        """,
                        [],
                        req.TargetUri);
                }

                if (req.TargetUri.AbsolutePath == "/movies/featured")
                {
                    Assert.Equal("POST", req.Method);
                    // inheritinputs: false -> must NOT contain sort=seeders
                    Assert.DoesNotContain("sort=seeders", req.TargetUri.Query);
                    var postMap = (req.FormFields ?? []).ToDictionary(k => k.Key, v => v.Value);
                    Assert.False(postMap.ContainsKey("sort"));
                    Assert.Equal("amelie-director-s-cut", postMap["q"]);
                    Assert.Equal("featured", postMap["mode"]);

                    // Returns 1 duplicate infohash (1111...) and 1 unique featured UHD release (ffff...)
                    return new IndexerHttpResponse(
                        200,
                        """
                        <html><body><table id="releases"><tbody>
                          <tr>
                            <td class="cat">mov_uhd</td>
                            <td class="title"><div class="release-line">Amelie.2001.2160p.UHD.BluRay.REMUX-GRP</div></td>
                            <td class="links"><a class="torrent-dl" href="/dl.php?info_hash=1111222233334444555566667777888899990000&id=1">DL</a></td>
                            <td class="size">52.0 GiB</td>
                            <td class="seed">120</td>
                            <td class="leech">10</td>
                            <td class="added">2 hours ago</td>
                          </tr>
                          <tr>
                            <td class="cat">mov_uhd</td>
                            <td class="title"><div class="release-line">Amelie.2001.2160p.WEB-DL.DV.HEVC-FEAT</div></td>
                            <td class="links"><a class="torrent-dl" href="/dl.php?info_hash=ffff0000eeee1111dddd2222cccc3333bbbb4444&id=3">DL</a></td>
                            <td class="size">24.0 GiB</td>
                            <td class="seed">40</td>
                            <td class="leech">2</td>
                            <td class="added">Yesterday at 14:00</td>
                          </tr>
                        </tbody></table></body></html>
                        """,
                        [],
                        req.TargetUri);
                }

                throw new InvalidOperationException($"Unexpected path '{req.TargetUri.AbsolutePath}' — tv/search should be skipped for movie categories!");
            },
        };

        var service = new UnifiedIndexerService(_dbFactory, _protector, catalog, gateway);
        await service.CreateIndexerAsync(new UpsertIndexerRequest(
            Name: "DualPath Public",
            Implementation: "cardigann",
            DefinitionId: "multipath-html-tracker",
            Protocol: "torrent",
            BaseUrl: "https://dualpath.example",
            RequestDelayMs: 0));

        var beforeUtc = DateTimeOffset.UtcNow;
        var releases = await service.SearchAsync(new IndexerSearchQuery(
            Query: "Amélie: Director's Cut!",
            MediaVertical: MediaVerticals.Movies,
            Categories: [2040, 2045]));

        // Only 2 paths ran (/movies/search and /movies/featured); /tv/search was filtered out by categories.
        // Duplicate infohash 1111... was deduplicated -> 3 unique releases total!
        Assert.Equal(2, gateway.SentRequests.Count);
        Assert.Equal(3, releases.Count);

        var remux = releases.First(r => r.InfoHash == "1111222233334444555566667777888899990000");
        Assert.Equal("Amelie.2001.2160p.UHD.BluRay.REMUX-GRP", remux.Title);
        Assert.Equal(0.0, remux.DownloadVolumeFactor);
        Assert.Equal(2.0, remux.UploadVolumeFactor);
        Assert.InRange((beforeUtc - remux.PublishedAt).TotalHours, 1.9, 2.2);

        var hd = releases.First(r => r.InfoHash == "aaaabbbbccccddddeeeeffff0000111122223333");
        Assert.Equal("Amelie.2001.1080p.BluRay.x264-GRP", hd.Title);
        Assert.Equal(0.5, hd.DownloadVolumeFactor);
        Assert.Equal(1.0, hd.UploadVolumeFactor);
        Assert.InRange((beforeUtc - hd.PublishedAt).TotalDays, 2.9, 3.2);

        var featured = releases.First(r => r.InfoHash == "ffff0000eeee1111dddd2222cccc3333bbbb4444");
        Assert.Equal(1.0, featured.DownloadVolumeFactor);
        Assert.Equal(1.0, featured.UploadVolumeFactor);
        Assert.Equal(14, featured.PublishedAt.Hour);
    }

    // -------------------------------------------------------------------------
    // Fixture 5: Explicit TestIndexerAsync diagnostics on login error selector match,
    // unsupported interactive CAPTCHA login, and invalid CSS selectors
    // -------------------------------------------------------------------------
    [Fact]
    public async Task Fixture5_TestIndexer_FailsClearlyOnLoginErrorSelector_InteractiveCaptcha_AndInvalidSelector()
    {
        // 5A: Login error selector match extracts human-readable tracker error message
        const string errorLoginYaml = """
            id: tracker-login-error
            name: StrictAuth Tracker
            type: private
            links:
              - https://strictauth.lan/
            login:
              path: login
              method: post
              inputs:
                username: "{{ .Config.username }}"
                password: "{{ .Config.password }}"
              error:
                - selector: div.login-error-banner
                  message:
                    selector: div.login-error-banner span.reason
            search:
              paths:
                - path: browse
              rows:
                selector: tr.row
              fields:
                title:
                  selector: td.t
                download:
                  selector: a.dl
                  attribute: href
            """;

        // 5B: Interactive CAPTCHA login without cookie/FlareSolverr
        const string captchaLoginYaml = """
            id: tracker-interactive-captcha
            name: CaptchaGate Tracker
            type: private
            links:
              - https://captchagate.lan/
            login:
              path: login.php
              method: form
              form: form#login
              captcha:
                type: image
                selector: img#captcha-image
                input: captcha_text
              inputs:
                username: "{{ .Config.username }}"
                password: "{{ .Config.password }}"
            search:
              paths:
                - path: torrents.php
              rows:
                selector: tr
              fields:
                title:
                  selector: td.name
                download:
                  selector: a.dl
                  attribute: href
            """;

        // 5C: Broken CSS selector in custom definition
        const string brokenSelectorYaml = """
            id: tracker-broken-selector
            name: BrokenSelector Tracker
            type: public
            links:
              - https://broken.lan/
            search:
              paths:
                - path: browse
              rows:
                selector: "table#torrents tr[[invalid"
              fields:
                title:
                  selector: td.name
                download:
                  selector: a.dl
                  attribute: href
            """;

        await File.WriteAllTextAsync(Path.Combine(_paths.CustomDefinitionsDirectory, "tracker-login-error.yml"), errorLoginYaml);
        await File.WriteAllTextAsync(Path.Combine(_paths.CustomDefinitionsDirectory, "tracker-interactive-captcha.yml"), captchaLoginYaml);
        await File.WriteAllTextAsync(Path.Combine(_paths.CustomDefinitionsDirectory, "tracker-broken-selector.yml"), brokenSelectorYaml);

        var catalog = new CardigannDefinitionCatalog(_paths);
        var gateway = new RecordingCardigannHttpGateway
        {
            SendHandler = (req, _) => new IndexerHttpResponse(
                200,
                """
                <html><body>
                  <div class="login-error-banner">
                    <strong>Access Denied:</strong>
                    <span class="reason">Invalid password or 2FA hardware key required.</span>
                  </div>
                </body></html>
                """,
                [],
                req.TargetUri),
        };

        var service = new UnifiedIndexerService(_dbFactory, _protector, catalog, gateway);

        var loginErrResult = await service.TestIndexerAsync(new UpsertIndexerRequest(
            Name: "StrictAuth",
            Implementation: "cardigann",
            DefinitionId: "tracker-login-error",
            Protocol: "torrent",
            BaseUrl: "https://strictauth.lan",
            ExtraSettings: new Dictionary<string, string>
            {
                ["username"] = "trackeruser",
                ["password"] = "wrong-pass",
            }));

        Assert.False(loginErrResult.Ok);
        Assert.Contains("Invalid password or 2FA hardware key required.", loginErrResult.Message);

        var captchaResult = await service.TestIndexerAsync(new UpsertIndexerRequest(
            Name: "CaptchaGate",
            Implementation: "cardigann",
            DefinitionId: "tracker-interactive-captcha",
            Protocol: "torrent",
            BaseUrl: "https://captchagate.lan",
            ExtraSettings: new Dictionary<string, string>
            {
                ["username"] = "trackeruser",
                ["password"] = "pass",
            }));

        Assert.False(captchaResult.Ok);
        Assert.Contains("CAPTCHA", captchaResult.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("image", captchaResult.Message, StringComparison.OrdinalIgnoreCase);

        var brokenSelectorResult = await service.TestIndexerAsync(new UpsertIndexerRequest(
            Name: "BrokenSelector",
            Implementation: "cardigann",
            DefinitionId: "tracker-broken-selector",
            Protocol: "torrent",
            BaseUrl: "https://broken.lan"));

        Assert.False(brokenSelectorResult.Ok);
        Assert.Contains("Invalid CSS selector", brokenSelectorResult.Message);
    }

    // -------------------------------------------------------------------------
    // Additional Parity Coverage: rows.after, rows.dateheaders, $ root JSON, XML/RSS
    // -------------------------------------------------------------------------
    [Fact]
    public void RowsAfterMerging_RowsDateHeaders_RootJsonArray_AndXmlResponse_ExtractCleanly()
    {
        // 1. HTML rows.after + rows.dateheaders
        const string htmlDefYml = """
            id: merged-rows-tracker
            name: MergedRows Tracker
            type: public
            links:
              - https://merged.example/
            search:
              paths:
                - path: browse.php
              rows:
                selector: "table#list tr.item-main, table#list tr.item-detail"
                after: 1
                dateheaders:
                  selector: "tr.date-group"
                  filters:
                    - name: re_replace
                      args: ["^Added:\\s*", ""]
                    - name: dateparse
                      args: "02.01.2006"
              fields:
                title:
                  selector: "td.main-title a"
                download:
                  selector: "td.detail-links a.dl"
                  attribute: href
                size:
                  selector: "td.detail-size"
                seeders:
                  selector: "td.detail-seeders"
            """;

        var htmlDef = CardigannDefinitionCatalog.TryParseYamlText(htmlDefYml)!;
        Assert.NotNull(htmlDef);

        const string htmlPayload = """
            <html><body><table id="list">
              <tr class="date-group"><td colspan="3">Added: 14.10.2026</td></tr>
              <tr class="item-main"><td class="main-title"><a href="/t/1">Severance.S02E01.2160p.WEB-DL</a></td></tr>
              <tr class="item-detail">
                <td class="detail-links"><a class="dl" href="/dl/1.torrent">Torrent</a></td>
                <td class="detail-size">8.5 GiB</td>
                <td class="detail-seeders">99</td>
              </tr>
              <tr class="date-group"><td colspan="3">Added: 12.10.2026</td></tr>
              <tr class="item-main"><td class="main-title"><a href="/t/2">Severance.S02E02.2160p.WEB-DL</a></td></tr>
              <tr class="item-detail">
                <td class="detail-links"><a class="dl" href="/dl/2.torrent">Torrent</a></td>
                <td class="detail-size">7.2 GiB</td>
                <td class="detail-seeders">75</td>
              </tr>
            </table></body></html>
            """;

        var catalog = new CardigannDefinitionCatalog(_paths);
        var htmlRows = catalog.ExtractReleasesFromPayload(htmlDef, htmlPayload, new Uri("https://merged.example/"));
        Assert.Equal(2, htmlRows.Count);
        Assert.Equal("Severance.S02E01.2160p.WEB-DL", htmlRows[0].Title);
        Assert.Equal("https://merged.example/dl/1.torrent", htmlRows[0].DownloadUrl);
        Assert.Equal(99, htmlRows[0].Seeders);
        Assert.Equal(new DateTimeOffset(2026, 10, 14, 0, 0, 0, TimeSpan.Zero), htmlRows[0].PublishedAt);
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero), htmlRows[1].PublishedAt);

        // 2. Root JSON array ($)
        const string rootJsonYml = """
            id: root-json
            name: RootJson
            links: ["https://json.example/"]
            search:
              paths:
                - path: api
                  response:
                    type: json
              rows:
                selector: "$"
              fields:
                title:
                  selector: name
                infohash:
                  selector: info_hash
                size:
                  selector: num_bytes
            """;
        var rootJsonDef = CardigannDefinitionCatalog.TryParseYamlText(rootJsonYml)!;
        var rootJsonRows = catalog.ExtractReleasesFromPayload(
            rootJsonDef,
            """[{"name":"Ubuntu.24.04.ISO","info_hash":"0123456789abcdef0123456789abcdef01234567","num_bytes":4294967296}]""",
            new Uri("https://json.example/"));
        var jsonRow = Assert.Single(rootJsonRows);
        Assert.Equal("Ubuntu.24.04.ISO", jsonRow.Title);
        Assert.Equal("0123456789abcdef0123456789abcdef01234567", jsonRow.InfoHash);
        Assert.StartsWith("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567", jsonRow.DownloadUrl);

        // 3. XML response.type: xml
        const string xmlYml = """
            id: xml-rss-tracker
            name: XmlRssTracker
            links: ["https://xml.example/"]
            search:
              paths:
                - path: rss.xml
                  response:
                    type: xml
              rows:
                selector: channel > item
              fields:
                title:
                  selector: title
                download:
                  selector: enclosure
                  attribute: url
                size:
                  selector: enclosure
                  attribute: length
                seeders:
                  selector: "torznab:attr[name=\"seeders\"]"
                  attribute: value
            """;
        var xmlDef = CardigannDefinitionCatalog.TryParseYamlText(xmlYml)!;
        const string xmlPayload = """
            <?xml version="1.0" encoding="UTF-8"?>
            <rss version="2.0" xmlns:torznab="http://torznab.com/schemas/2015/feed">
              <channel>
                <item>
                  <title>Foundation.S03E01.2160p.WEB-DL</title>
                  <enclosure url="https://xml.example/dl/foundation.torrent" length="5368709120" />
                  <torznab:attr name="seeders" value="155" />
                </item>
              </channel>
            </rss>
            """;
        var xmlRows = catalog.ExtractReleasesFromPayload(xmlDef, xmlPayload, new Uri("https://xml.example/"));
        var xmlRow = Assert.Single(xmlRows);
        Assert.Equal("Foundation.S03E01.2160p.WEB-DL", xmlRow.Title);
        Assert.Equal("https://xml.example/dl/foundation.torrent", xmlRow.DownloadUrl);
        Assert.Equal(5368709120L, xmlRow.SizeBytes);
        Assert.Equal(155, xmlRow.Seeders);
    }

    // -------------------------------------------------------------------------
    // Template Evaluator & Filter Pipeline Unit Coverage
    // -------------------------------------------------------------------------
    [Fact]
    public void TemplateEvaluator_SupportsAllContextVariables_ControlFlow_Range_AndPipelines()
    {
        var query = new IndexerSearchQuery(
            Query: "The Expanse",
            MediaVertical: MediaVerticals.Tv,
            ImdbId: "tt3230854",
            TmdbId: 63639,
            TvdbId: 280619,
            Season: 4,
            Episode: 8,
            Series: "The Expanse",
            Album: "Soundtrack",
            Artist: "Clinton Shorter",
            Author: "James S. A. Corey",
            Title: "Cibola Burn",
            Year: 2019);

        var ctx = CardigannTemplateContext.Create(
            def: null,
            baseUrl: "https://tracker.unit",
            apiKey: "key123",
            extraSettings: new Dictionary<string, string>
            {
                ["sort"] = "seeders",
                ["order"] = "desc",
            },
            query: query,
            trackerCategories: ["5040", "5045"],
            nowUtc: new DateTimeOffset(2026, 11, 7, 18, 0, 0, TimeSpan.Zero));

        // Context variables & printf formatting
        var varsRendered = CardigannTemplateEvaluator.Evaluate(
            "{{ .Query.Q }}|{{ .Keywords }}|{{ printf \"S%02dE%02d\" .Query.Season .Query.Ep }}|{{ .Query.Series }}|{{ .Query.Album }}|{{ .Query.Artist }}|{{ .Query.Author }}|{{ .Query.Title }}|{{ .Query.Year }}|{{ .Query.IMDBID }}|{{ .Query.IMDBIDShort }}|{{ .Query.TMDBID }}|{{ .Query.TVDBID }}|{{ .Today.Year }}",
            ctx);
        Assert.Equal(
            "The Expanse|The Expanse S04E08|S04E08|The Expanse|Soundtrack|Clinton Shorter|James S. A. Corey|Cibola Burn|2019|tt3230854|3230854|63639|280619|2026",
            varsRendered);

        // Control blocks: if / else if / else + and / or / not / eq / ne / gt / lt + range .Categories
        const string controlTpl = """
            {{- if and (ne .Query.TVDBID "") (gt .Query.Season 2) (lt .Query.Ep 10) -}}
            tv:{{ .Query.TVDBID }}
            {{- else if eq .Config.sort "size" -}}
            size
            {{- else -}}
            fallback
            {{- end -}}
            |{{ range .Categories }}c{{ . }}=1&{{ end }}
            """;
        var controlRendered = CardigannTemplateEvaluator.Evaluate(controlTpl, ctx).Trim();
        Assert.Equal("tv:280619|c5040=1&c5045=1&", controlRendered);

        // Pipeline functions: tolower, toupper, replace, re_replace, trim, urlencode, join
        var pipeRendered = CardigannTemplateEvaluator.Evaluate(
            "{{ .Query.Author | tolower | re_replace \"[^a-z0-9]+\" \"_\" | trim \"_\" }}|{{ toupper .Config.order }}|{{ replace .Query.Q \" \" \"+\" }}|{{ urlencode \"a b&c\" }}|{{ .Categories | join \";\" }}",
            ctx);
        Assert.Equal("james_s_a_corey|DESC|The+Expanse|a%20b%26c|5040;5045", pipeRendered);
    }

    [Fact]
    public void FilterPipeline_SupportsAllCoreCardigannFilters()
    {
        var refUtc = new DateTimeOffset(2026, 11, 7, 15, 0, 0, TimeSpan.Zero);

        Assert.Equal("foo.bar", CardigannFilterPipeline.ApplyFilter("foo_bar", "replace", ["_", "."]));
        Assert.Equal("Dune 2024", CardigannFilterPipeline.ApplyFilter("[GRP] Dune (2024)", "re_replace", [@"^\[[^\]]+\]\s*|\(|\)", ""]));
        Assert.Equal("90210", CardigannFilterPipeline.ApplyFilter("/torrents.php?id=90210&hit=1", "regexp", [@"id=(\d+)"]));
        Assert.Equal("part3", CardigannFilterPipeline.ApplyFilter("part1/part2/part3", "split", ["/", "-1"]));
        Assert.Equal("clean", CardigannFilterPipeline.ApplyFilter("--clean--", "trim", ["-"]));
        Assert.Equal("https://x.lan/dl/1", CardigannFilterPipeline.ApplyFilter("dl/1", "prepend", ["https://x.lan/"]));
        Assert.Equal("dl/1.torrent", CardigannFilterPipeline.ApplyFilter("dl/1", "append", [".torrent"]));
        Assert.Equal("hello world", CardigannFilterPipeline.ApplyFilter("Hello%20World", "urldecode", [" "]).ToLowerInvariant());
        Assert.Equal("HELLO%20WORLD", CardigannFilterPipeline.ApplyFilter("hello world", "toupper", []) is var up ? CardigannFilterPipeline.ApplyFilter(up, "urlencode", []) : "");
        Assert.Equal("abcdef1234", CardigannFilterPipeline.ApplyFilter("https://t.lan/dl?foo=1&hash=abcdef1234", "querystring", ["hash"]));
        Assert.Equal("unchanged", CardigannFilterPipeline.ApplyFilter("unchanged", "hexdump", []));
        Assert.Equal("Movie Title 2026", CardigannFilterPipeline.ApplyFilter("Movie: Title? *2026* /\\", "validfilename", []));
        Assert.Equal("Munchen Strasse &AEther Lodz", CardigannFilterPipeline.ApplyFilter("München Straße &Æther Łódź", "diacritics", []));
        Assert.Equal("1.5 GiB", CardigannFilterPipeline.ApplyFilter("1610612736", "humanize", []));

        var goDateIso = CardigannFilterPipeline.ApplyFilter("07 Nov 2026 14:05:09 +0100", "dateparse", ["02 Jan 2006 15:04:05 -0700"]);
        Assert.Equal(new DateTimeOffset(2026, 11, 7, 13, 5, 9, TimeSpan.Zero), DateTimeOffset.Parse(goDateIso));

        var fuzzyIso = CardigannFilterPipeline.ApplyFilter("Yesterday at 09:30", "fuzzytime", [], refUtc);
        Assert.Equal(new DateTimeOffset(2026, 11, 6, 9, 30, 0, TimeSpan.Zero), DateTimeOffset.Parse(fuzzyIso));
    }

    // -------------------------------------------------------------------------
    // Real Loopback Verification of IndexerHttpGateway (Redirects + Cookies + Headers + POST + FlareSolverr + HTTP Proxy)
    // -------------------------------------------------------------------------
    [Fact]
    public async Task RealIndexerHttpGateway_ForwardsCookiesHeadersPostRedirects_FlareSolverrPost_AndHttpProxy()
    {
        var safeFactory = new SafeHttpClientFactory(Options.Create(new SafeHttpEgressOptions()));
        var gateway = new IndexerHttpGateway(safeFactory);

        // 1. Direct loopback server testing POST -> 302 Redirect with Set-Cookie -> GET with accumulated Cookie & custom Header
        using var originListener = new TcpListener(IPAddress.Loopback, 0);
        originListener.Start();
        var originPort = ((IPEndPoint)originListener.LocalEndpoint).Port;

        var originTask = Task.Run(async () =>
        {
            // Hop 1: POST /login -> returns 302 + Set-Cookie: auth_sid=loopback99
            using (var client1 = await originListener.AcceptTcpClientAsync())
            await using (var stream1 = client1.GetStream())
            {
                var reqText1 = await ReadRawHttpRequestAsync(stream1);
                Assert.Contains("POST /login HTTP/1.1", reqText1);
                Assert.Contains("X-Custom-Token: alpha-77", reqText1);
                Assert.Contains("Cookie: init_c=1", reqText1);
                Assert.Contains("user=neo&pass=trinity", reqText1);

                var resp1 =
                    "HTTP/1.1 302 Found\r\n" +
                    "Set-Cookie: auth_sid=loopback99; Path=/; HttpOnly\r\n" +
                    $"Location: http://127.0.0.1:{originPort}/dashboard\r\n" +
                    "Content-Length: 0\r\n" +
                    "Connection: close\r\n\r\n";
                await stream1.WriteAsync(Encoding.UTF8.GetBytes(resp1));
            }

            // Hop 2: Redirected GET /dashboard -> verifies both init_c=1 and auth_sid=loopback99 are forwarded!
            using (var client2 = await originListener.AcceptTcpClientAsync())
            await using (var stream2 = client2.GetStream())
            {
                var reqText2 = await ReadRawHttpRequestAsync(stream2);
                Assert.Contains("GET /dashboard HTTP/1.1", reqText2);
                Assert.Contains("init_c=1", reqText2);
                Assert.Contains("auth_sid=loopback99", reqText2);

                const string body = "authenticated-dashboard-ok";
                var resp2 =
                    "HTTP/1.1 200 OK\r\n" +
                    "Content-Type: text/plain; charset=utf-8\r\n" +
                    $"Content-Length: {body.Length}\r\n" +
                    "Connection: close\r\n\r\n" +
                    body;
                await stream2.WriteAsync(Encoding.UTF8.GetBytes(resp2));
            }
        });

        var directResp = await gateway.SendAsync(
            new IndexerHttpRequest(
                TargetUri: new Uri($"http://127.0.0.1:{originPort}/login"),
                Method: "POST",
                Headers: new Dictionary<string, string> { ["X-Custom-Token"] = "alpha-77" },
                FormFields: [new KeyValuePair<string, string>("user", "neo"), new KeyValuePair<string, string>("pass", "trinity")],
                CookieHeader: "init_c=1"),
            matchedProxy: null);

        await originTask;
        Assert.Equal(200, directResp.StatusCode);
        Assert.Equal("authenticated-dashboard-ok", directResp.Body);
        Assert.Contains(directResp.SetCookieHeaders, c => c.Contains("auth_sid=loopback99"));

        // 2. Mock FlareSolverr loopback server verifying request.post + cookies + postData + solution.cookies extraction
        using var fsListener = new TcpListener(IPAddress.Loopback, 0);
        fsListener.Start();
        var fsPort = ((IPEndPoint)fsListener.LocalEndpoint).Port;

        var fsTask = Task.Run(async () =>
        {
            using var fsClient = await fsListener.AcceptTcpClientAsync();
            await using var fsStream = fsClient.GetStream();
            var rawFsReq = await ReadRawHttpRequestAsync(fsStream);
            var bodyIdx = rawFsReq.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            var jsonBody = rawFsReq[(bodyIdx + 4)..];
            using var parsed = JsonDocument.Parse(jsonBody);
            Assert.Equal("request.post", parsed.RootElement.GetProperty("cmd").GetString());
            Assert.Equal("q=dune&cat=2045", parsed.RootElement.GetProperty("postData").GetString());
            Assert.Equal("sid", parsed.RootElement.GetProperty("cookies")[0].GetProperty("name").GetString());
            Assert.Equal("abc", parsed.RootElement.GetProperty("cookies")[0].GetProperty("value").GetString());

            const string fsSolution = """
                {
                  "status": "ok",
                  "solution": {
                    "url": "https://cf-tracker.example/torrents",
                    "status": 200,
                    "response": "<html>cf-bypassed</html>",
                    "cookies": [
                      { "name": "cf_clearance", "value": "solved_token_123" }
                    ]
                  }
                }
                """;
            var httpResp =
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: application/json\r\n" +
                $"Content-Length: {Encoding.UTF8.GetByteCount(fsSolution)}\r\n" +
                "Connection: close\r\n\r\n" +
                fsSolution;
            await fsStream.WriteAsync(Encoding.UTF8.GetBytes(httpResp));
        });

        var fsResp = await gateway.SendAsync(
            new IndexerHttpRequest(
                TargetUri: new Uri("https://cf-tracker.example/torrents"),
                Method: "POST",
                FormFields: [new KeyValuePair<string, string>("q", "dune"), new KeyValuePair<string, string>("cat", "2045")],
                CookieHeader: "sid=abc"),
            new IndexerProxyInternal("flaresolverr", "127.0.0.1", fsPort, null, null, 30));

        await fsTask;
        Assert.Equal("<html>cf-bypassed</html>", fsResp.Body);
        Assert.Contains("cf_clearance=solved_token_123", fsResp.SetCookieHeaders);

        // 3. Loopback HTTP forward proxy verifying proxy routing + Cookie & Header forwarding
        using var proxyListener = new TcpListener(IPAddress.Loopback, 0);
        proxyListener.Start();
        var proxyPort = ((IPEndPoint)proxyListener.LocalEndpoint).Port;

        var proxyTask = Task.Run(async () =>
        {
            using var pxClient = await proxyListener.AcceptTcpClientAsync();
            await using var pxStream = pxClient.GetStream();
            var rawPxReq = await ReadRawHttpRequestAsync(pxStream);
            Assert.Contains("GET http://proxied-tracker.example/feed HTTP/1.1", rawPxReq);
            Assert.Contains("Cookie: px_cookie=ok", rawPxReq);

            const string pxBody = "via-http-proxy-ok";
            var pxHttpResp =
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: text/plain\r\n" +
                $"Content-Length: {pxBody.Length}\r\n" +
                "Connection: close\r\n\r\n" +
                pxBody;
            await pxStream.WriteAsync(Encoding.UTF8.GetBytes(pxHttpResp));
        });

        var proxiedResp = await gateway.SendAsync(
            new IndexerHttpRequest(
                TargetUri: new Uri("http://proxied-tracker.example/feed"),
                Method: "GET",
                CookieHeader: "px_cookie=ok"),
            new IndexerProxyInternal("http", "127.0.0.1", proxyPort, null, null, 30));

        await proxyTask;
        Assert.Equal("via-http-proxy-ok", proxiedResp.Body);

        // 4. Cross-origin redirect (127.0.0.1 -> localhost) strips Cookie and Authorization headers on the external hop
        using var crossOriginListener = new TcpListener(IPAddress.Loopback, 0);
        crossOriginListener.Start();
        var crossPort = ((IPEndPoint)crossOriginListener.LocalEndpoint).Port;

        var crossTask = Task.Run(async () =>
        {
            // Hop 1 at 127.0.0.1 receives Cookie and Authorization, redirects to localhost (different host string)
            using (var c1 = await crossOriginListener.AcceptTcpClientAsync())
            await using (var s1 = c1.GetStream())
            {
                var req1 = await ReadRawHttpRequestAsync(s1);
                Assert.Contains("Cookie: tracker_secret=topsecret", req1);
                Assert.Contains("Authorization: Bearer secret-jwt", req1);

                var redirResp =
                    "HTTP/1.1 302 Found\r\n" +
                    $"Location: http://localhost:{crossPort}/cdn-Landing\r\n" +
                    "Content-Length: 0\r\n" +
                    "Connection: close\r\n\r\n";
                await s1.WriteAsync(Encoding.UTF8.GetBytes(redirResp));
            }

            // Hop 2 at localhost MUST NOT receive Cookie or Authorization headers!
            using (var c2 = await crossOriginListener.AcceptTcpClientAsync())
            await using (var s2 = c2.GetStream())
            {
                var req2 = await ReadRawHttpRequestAsync(s2);
                Assert.DoesNotContain("tracker_secret", req2);
                Assert.DoesNotContain("Cookie:", req2, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("Authorization:", req2, StringComparison.OrdinalIgnoreCase);

                const string cdnBody = "external-cdn-ok";
                var okResp =
                    "HTTP/1.1 200 OK\r\n" +
                    "Content-Type: text/plain\r\n" +
                    $"Content-Length: {cdnBody.Length}\r\n" +
                    "Connection: close\r\n\r\n" +
                    cdnBody;
                await s2.WriteAsync(Encoding.UTF8.GetBytes(okResp));
            }
        });

        var crossResp = await gateway.SendAsync(
            new IndexerHttpRequest(
                TargetUri: new Uri($"http://127.0.0.1:{crossPort}/dl"),
                Method: "GET",
                Headers: new Dictionary<string, string> { ["Authorization"] = "Bearer secret-jwt" },
                CookieHeader: "tracker_secret=topsecret"),
            matchedProxy: null);

        await crossTask;
        Assert.Equal("external-cdn-ok", crossResp.Body);

        // 5. Template step budget guard prevents runaway nested range evaluation (> 10,000 steps)
        var hugeCategories = Enumerable.Range(1, 200).Select(i => i.ToString()).ToList();
        var runawayCtx = new CardigannTemplateContext { Categories = hugeCategories };
        const string runawayTpl = "{{ range .Categories }}{{ range $.Categories }}x{{ end }}{{ end }}";
        Assert.Throws<CardigannConfigException>(() => CardigannTemplateEvaluator.Evaluate(runawayTpl, runawayCtx));
    }

    private static async Task<string> ReadRawHttpRequestAsync(NetworkStream stream)
    {
        var buffer = new byte[4096];
        var ms = new MemoryStream();
        var headerEnd = -1;
        var contentLength = 0;

        while (true)
        {
            var read = await stream.ReadAsync(buffer);
            if (read <= 0)
            {
                break;
            }
            ms.Write(buffer, 0, read);

            var currentText = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
            if (headerEnd < 0)
            {
                var idx = currentText.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (idx >= 0)
                {
                    headerEnd = Encoding.UTF8.GetByteCount(currentText[..(idx + 4)]);
                    foreach (var line in currentText[..idx].Split("\r\n"))
                    {
                        if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) &&
                            int.TryParse(line["Content-Length:".Length..].Trim(), out var cl))
                        {
                            contentLength = cl;
                        }
                    }
                }
            }

            if (headerEnd >= 0 && ms.Length >= headerEnd + contentLength)
            {
                break;
            }
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
