# Model Context Protocol (MCP) Server & AI Agent Guide

Managerr provides a streamable HTTP **Model Context Protocol (MCP)** server at `/api/v1/mcp` and `/mcp`. External agents and tools (such as Google Antigravity, Claude Desktop, Claude Code, and Cursor) can use this endpoint to inspect, search, download, verify, and manage lifecycle rules across media libraries.

Administrators can enable or disable the MCP endpoints at runtime via the `system.mcp.enabled` setting (in **Settings -> General** or via the settings API; enabled by default). When disabled, MCP endpoints reject incoming requests cleanly without exposing tools.

---

## 1. Streamable HTTP Transport & Protocol Architecture

The Managerr MCP server implements the official **Model Context Protocol specification** using standard JSON-RPC 2.0 over HTTP:

* **Mount Endpoints:** `/api/v1/mcp` and `/mcp`.
* **Transport Mode:** Streamable HTTP POST.
* **Batch Processing:** Accepts both single JSON-RPC objects (`{ "jsonrpc": "2.0", ... }`) and batch arrays (`[ { ... }, { ... } ]`).
* **HTTP Status Code Contract:**
  * `200 OK`: Successful synchronous execution returning JSON-RPC result or error payloads.
  * `202 Accepted`: Returned when incoming requests contain only JSON-RPC notifications (messages without an `id` field).
  * `204 NoContent`: Returned on `DELETE` requests to gracefully clean up agent sessions.
  * `400 Bad Request`: Returned on malformed JSON (`-32700 Parse error`) or invalid JSON-RPC structures (`-32600 Invalid Request`).
  * `405 Method Not Allowed`: Returned on `GET` requests with header `Allow: POST, DELETE` to enforce streamable spec compliance.
* **Session Tracking:** If the client provides an `Mcp-Session-Id` header, Managerr echoes the header on all responses to maintain correlation across multi-turn sessions.

---

## 2. Protocol Version Negotiation

During the initial handshake (`initialize` method), Managerr negotiates the protocol version with the client:

* **Supported Versions:**
  * `2025-06-18`
  * `2025-03-26` *(Default Fallback)*
  * `2024-11-05`
* **Handshake Negotiation:** If the client requests a supported version in `params.protocolVersion`, Managerr adopts that version. If the client requests an unsupported or legacy version, Managerr gracefully falls back to `2025-03-26`.
* **Declared Capabilities:** Managerr declares `tools` capability with `{ "listChanged": false }`. Prompts and resources endpoints return valid empty collections for MCP client compliance.

---

## 3. Zero-Trust RBAC Filtering & Security Model

Managerr enforces Role-Based Access Control (RBAC) across all MCP operations:

### 3.1 Tool Discovery Filtering (`tools/list`)
When an AI agent queries `tools/list`, Managerr checks the active scopes associated with the agent's authentication token ([Authentication & RBAC](./02-SECURITY-AND-API-SPEC.md)).
* **Least-Privilege Visibility:** Tools requiring scopes that the agent does not possess are **filtered out of the response**. Disallowed tools are excluded from the returned list.
* **Context Token Efficiency:** Minimizes LLM system prompt token consumption by omitting irrelevant or disallowed tools.

### 3.2 Runtime Execution Boundary (`tools/call`)
If an agent attempts to invoke a tool directly without holding the required scopes, Managerr rejects the invocation and returns an error payload inside the JSON-RPC result:
```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "result": {
    "isError": true,
    "content": [
      {
        "type": "text",
        "text": "{\"error\":\"ERR_RBAC_FORBIDDEN\",\"message\":\"Insufficient permissions for 'catalog_delete_work'. Required any of: catalog:delete.\"}"
      }
    ]
  }
}
```

---

## 4. Least-Privilege Agent API Key Recipes

To follow security best practices, generate scoped machine API keys dedicated to specific agent roles rather than providing administrative credentials.

### Recipe 1: Catalog Browser & Search Agent
* **Role:** Read-only research, media discovery, and indexer release inspection without modification permissions.
* **Required Scopes:**
  * `catalog:read`
  * `indexers:search`
* **Visible Tools:** `libraries_list`, `catalog_list_works`, `catalog_get_work`, `metadata_search`, `wanted_list`, `releases_interactive_search`, `profiles_list`.

### Recipe 2: Media Butler / Request Assistant
* **Role:** Interactive user-facing butler capable of browsing media, submitting requests, auto-classifying titles, and triggering automated wanted searches.
* **Required Scopes:**
  * `catalog:read`
  * `catalog:request`
  * `catalog:auto_approve`
  * `catalog:write`
  * `indexers:search`
* **Additional Tools Granted:** `catalog_add_work`, `catalog_update_work`, `catalog_set_monitoring`, `catalog_auto_classify`, `requests_manage`, `releases_auto_search`, `releases_grab`.

### Recipe 3: Automated Queue & Import Manager
* **Role:** Background downloader monitoring, manual import post-processing, and queue maintenance.
* **Required Scopes:**
  * `queue:read`
  * `queue:manage`
  * `downloaders:read`
* **Visible Tools:** `queue_list`, `queue_control`, `manual_import_preview`, `manual_import_execute`, `activity_history`, `activity_blocklist`.

### Recipe 4: Quarantine & Quality Auditor
* **Role:** Speech-to-text language verification auditor reviewing quarantined audio streams and resolving mislabeled releases.
* **Required Scopes:**
  * `quarantine:review`
  * `langverify:read`
  * `langverify:execute`
* **Visible Tools:** `quarantine_list`, `quarantine_resolve`.

---

## 5. Ready-to-Paste Client Configurations

Replace `<YOUR_API_KEY>` with your scoped Managerr API key and adjust the host URL as appropriate for your environment.

### 5.1 Claude Desktop (`claude_desktop_config.json`)
```json
{
  "mcpServers": {
    "managerr": {
      "command": "npx",
      "args": [
        "-y",
        "mcp-remote-client",
        "http://localhost:8989/api/v1/mcp",
        "--header",
        "X-Api-Key: <YOUR_API_KEY>"
      ]
    }
  }
}
```

### 5.2 Claude Code (`claude config`)
Run in your shell:
```bash
claude mcp add managerr http://localhost:8989/api/v1/mcp \
  --header "X-Api-Key: <YOUR_API_KEY>"
```

### 5.3 Cursor (`.cursor/mcp.json`)
```json
{
  "mcpServers": {
    "managerr": {
      "url": "http://localhost:8989/api/v1/mcp",
      "headers": {
        "X-Api-Key": "<YOUR_API_KEY>"
      }
    }
  }
}
```

### 5.4 Google Antigravity / Gemini CLI (`antigravity.json`)
```json
{
  "mcpServers": {
    "managerr": {
      "type": "http",
      "url": "http://localhost:8989/api/v1/mcp",
      "headers": {
        "X-Api-Key": "<YOUR_API_KEY>"
      }
    }
  }
}
```

---

## 6. MCP Tools Reference (40 Tools)

Managerr exposes 40 specialized MCP tools across three functional domains: **Catalog Management (12 tools)**, **Operations & Acquisition (18 tools)**, and **Cleanerr Lifecycle (10 tools)**.

### 6.1 Catalog Management Tools (1-12)

| # | Tool Name | Required Scope(s) | Annotations | Input Parameters | Operational Description & Behavior |
| :-: | :--- | :--- | :--- | :--- | :--- |
| **1** | `libraries_list` | `catalog:read` | `readOnlyHint: true`<br/>`destructiveHint: false`<br/>`idempotentHint: true` | *None* | Lists all accessible libraries across verticals with root folder filesystem paths and work counts. |
| **2** | `libraries_create` | `catalog:write` | `readOnlyHint: false`<br/>`destructiveHint: false`<br/>`idempotentHint: false` | `name` (string, required)<br/>`mediaType` (string, required: `movies`\|`tv`\|`anime`\|`music`\|`ebooks`\|`audiobooks`\|`comics`)<br/>`rootFolderPath` (string, required)<br/>`plexSectionId` (string, optional)<br/>`defaultProfileId` (integer, optional) | Creates a new media library root folder for `movies`, `tv`, `anime`, `music`, `ebooks`, `audiobooks`, or `comics`. |
| **3** | `catalog_list_works` | `catalog:read` | `readOnlyHint: true`<br/>`destructiveHint: false`<br/>`idempotentHint: true` | `q` (string)<br/>`libraryId` (integer)<br/>`libraryIds` (string, comma-separated)<br/>`vertical` (string, comma-separated)<br/>`monitored` (boolean)<br/>`availability` (string: `missing`\|`complete`\|`continuing`)<br/>`classification` (string: `anime-series`\|`anime-movie`\|`cartoon`\|`live-action-series`\|`live-action-movie`)<br/>`health` (string: `duplicates`\|`wrong-kind`\|`missing`\|`complete`\|`unmonitored`)<br/>`sort` (string: `title`\|`year`\|`missing`\|`addedAt`\|`updatedAt`)<br/>`order` (string: `asc`\|`desc`)<br/>`limit` (integer, default 50, max 500)<br/>`offset` (integer, default 0)<br/>`includeOverview` (boolean, default false) | Server-side filtered query across catalog works. Supports text search (title, alt titles, year, tags, external IDs), health/availability filtering, sorting, and pagination. |
| **4** | `catalog_get_work` | `catalog:read` | `readOnlyHint: true`<br/>`destructiveHint: false`<br/>`idempotentHint: true` | `workId` (integer, required)<br/>`seasonNumber` (integer, optional) | Returns the full hierarchical detail tree for a work: seasons/containers, episodes/units (optionally filtered to one season), attached media files, and language verification status. |
| **5** | `metadata_search` | `catalog:read` | `readOnlyHint: true`<br/>`destructiveHint: false`<br/>`idempotentHint: true` | `q` (string, required)<br/>`vertical` (string, default `all`: `all`\|`movies`\|`tv`\|`anime`\|`music`\|`ebooks`\|`audiobooks`\|`comics`)<br/>`providers` (string array, optional)<br/>`limit` (integer, default 10, max 25) | Searches upstream metadata providers (`tvdb`, `tmdb`, `anilist`, `musicbrainz`, `openlibrary`, etc.) by title or external ID (`imdb:...`, `anilist:...`) before adding a work to the catalog. |
| **6** | `catalog_add_work` | `catalog:write` | `readOnlyHint: false`<br/>`destructiveHint: false`<br/>`idempotentHint: false` | `query` (string, optional)<br/>`title` (string, optional; required if `query` omitted)<br/>`libraryId` (integer, optional)<br/>`vertical` (string, optional)<br/>`year` (integer, optional)<br/>`originalLanguage` (string, optional)<br/>`monitored` (boolean, default true)<br/>`monitorPreset` (string, optional: `all`\|`future`\|`missing`\|`existing`\|`firstSeason`\|`latestSeason`\|`none`)<br/>`qualityProfileId` (integer, optional)<br/>`languagePolicyId` (integer, optional)<br/>`minimumAvailability` (string, optional: `announced`\|`inCinemas`\|`released`)<br/>`tags` (string array, optional)<br/>`externalIds` (object map, optional)<br/>`autoSearch` (boolean, default false) | Adds a title to the catalog. If `query` is passed, metadata is auto-resolved and the best matching library is picked automatically. If `autoSearch=true` (requires `indexers:search`), immediately runs an automatic release search. |
| **7** | `catalog_update_work` | `catalog:write` | `readOnlyHint: false`<br/>`destructiveHint: false`<br/>`idempotentHint: true` | `workId` (integer, required)<br/>`title` (string)<br/>`monitored` (boolean)<br/>`monitorPreset` (string: `all`\|`future`\|`missing`\|`existing`\|`firstSeason`\|`latestSeason`\|`none`)<br/>`minimumAvailability` (string: `announced`\|`inCinemas`\|`released`)<br/>`qualityProfileId` (integer)<br/>`clearQualityProfile` (boolean)<br/>`languagePolicyId` (integer)<br/>`clearLanguagePolicy` (boolean)<br/>`originalLanguage` (string)<br/>`tags` (string array)<br/>`primaryProvider` (string)<br/>`episodeOrderingScheme` (string: `aired`\|`dvd`\|`absolute`) | Updates work-level settings, metadata bindings, episode ordering scheme, profile assignments, language policies, tags, or monitoring presets. |
| **8** | `catalog_set_monitoring` | `catalog:write` | `readOnlyHint: false`<br/>`destructiveHint: false`<br/>`idempotentHint: true` | `workId` (integer, required)<br/>`monitored` (boolean, required)<br/>`containerId` (integer, optional)<br/>`seasonNumber` (integer, optional)<br/>`cascadeUnits` (boolean, default true)<br/>`unitIds` (integer array, optional)<br/>`monitorPreset` (string, optional: `all`\|`future`\|`missing`\|`existing`\|`firstSeason`\|`latestSeason`\|`none`) | Toggles monitoring on a whole work, a specific season/container (`containerId` or `seasonNumber`), specific unit IDs (`unitIds`), or applies a work-level `monitorPreset`. |
| **9** | `catalog_refresh_metadata` | `catalog:write` | `readOnlyHint: false`<br/>`destructiveHint: false`<br/>`idempotentHint: true` | `workId` (integer, required)<br/>`switchProvider` (string, optional)<br/>`episodeOrderingScheme` (string, optional) | Refreshes upstream metadata for a work, optionally switching its primary metadata provider key or episode ordering scheme. |
| **10** | `catalog_auto_classify` | `catalog:write` | `readOnlyHint: false`<br/>`destructiveHint: false`<br/>`idempotentHint: true` | *None* | Runs the heuristic taxonomy classifier across catalog works to classify or update anime/cartoon/live-action work kinds and metadata tags. |
| **11** | `catalog_delete_work` | `catalog:delete` | `readOnlyHint: false`<br/>`destructiveHint: true`<br/>`idempotentHint: false` | `workId` (integer, required)<br/>`deleteFilesOnDisk` (boolean, default false) | Removes a work from the catalog and optionally deletes associated media files from disk. |
| **12** | `requests_manage` | `catalog:read`, `catalog:request`, `catalog:auto_approve`, `catalog:write` | `readOnlyHint: false`<br/>`destructiveHint: false`<br/>`idempotentHint: false` | `action` (string, default `list`: `list`\|`submit`\|`approve`\|`deny`)<br/>`libraryId` (integer, for `submit`)<br/>`title` (string, for `submit`)<br/>`vertical` (string, for `submit`)<br/>`requestedAudioLang` (string, optional, e.g. `de`, `en`)<br/>`requestedSubLang` (string, optional)<br/>`requestId` (integer, for `approve`\|`deny`) | Unified media request workflow: list requests, submit a user request (auto-approved if caller holds `catalog:auto_approve`), or approve/deny pending requests. |

---

### 6.2 Operations & Acquisition Tools (13-30)

| # | Tool Name | Required Scope(s) | Annotations | Input Parameters | Operational Description & Behavior |
| :-: | :--- | :--- | :--- | :--- | :--- |
| **13** | `releases_auto_search` | `indexers:search` | `readOnlyHint: false`<br/>`destructiveHint: false`<br/>`idempotentHint: false` | `workId` (integer, required)<br/>`containerId` (integer, optional)<br/>`unitId` (integer, optional; takes precedence over `containerId`) | Triggers automated multi-wave search and grab across indexers for missing or upgradable units. |
| **14** | `releases_interactive_search` | `indexers:search` | `readOnlyHint: true`<br/>`destructiveHint: false`<br/>`idempotentHint: true` | `workId` (integer, optional)<br/>`containerId` (integer, optional)<br/>`unitId` (integer, optional)<br/>`query` (string, required when `workId` omitted)<br/>`vertical` (string, optional)<br/>`season` (integer, optional)<br/>`episode` (integer, optional)<br/>`q` (string, optional)<br/>`protocol` (string, optional: `torrent`\|`usenet`)<br/>`approvedOnly` (boolean, default false)<br/>`hideBlocklisted` (boolean, default true)<br/>`sort` (string, default `score`: `score`\|`age`\|`size`\|`seeders`\|`title`)<br/>`order` (string, default `desc`: `asc`\|`desc`)<br/>`limit` (integer, default 25, max 100) | Performs live indexer search and returns ranked release candidates with custom format breakdown, language matches, rejections, and total scores. |
| **15** | `releases_grab` | `indexers:search` | `readOnlyHint: false`<br/>`destructiveHint: false`<br/>`idempotentHint: false` | `title` (string, required)<br/>`protocol` (string, required: `torrent`\|`usenet`)<br/>`mediaVertical` (string, required)<br/>`downloadUrlOrMagnet` (string, required)<br/>`infoHash` (string, optional)<br/>`workId` (integer, optional)<br/>`containerId` (integer, optional)<br/>`unitIds` (integer array, optional)<br/>`indexerId` (integer, optional)<br/>`releaseGuid` (string, optional)<br/>`indexerName` (string, optional)<br/>`sizeBytes` (integer, optional)<br/>`forceGrab` (boolean, default false) | Dispatches a release grab to the appropriate configured download client (torrent or Usenet), optionally forcing past duplicate in-flight checks. |
| **16** | `wanted_list` | `catalog:read` | `readOnlyHint: true`<br/>`destructiveHint: false`<br/>`idempotentHint: true` | `mode` (string, default `missing`: `missing`\|`cutoff_unmet`)<br/>`page` (integer, default 1)<br/>`pageSize` (integer, default 50, max 200)<br/>`vertical` (string, optional)<br/>`libraryId` (integer, optional)<br/>`libraryIds` (string, comma-separated)<br/>`monitoredOnly` (boolean, default true)<br/>`releasedOnly` (boolean, default false)<br/>`language` (string, optional) | Lists monitored units that are missing media files or have not yet met quality/language profile cutoff thresholds. |
| **17** | `wanted_bulk_search` | `indexers:search` | `readOnlyHint: false`<br/>`destructiveHint: false`<br/>`idempotentHint: false` | `mode` (string, default `selected`: `selected`\|`missing`\|`cutoff`)<br/>`unitIds` (integer array, for `selected`)<br/>`vertical` (string, optional)<br/>`libraryId` (integer, optional)<br/>`libraryIds` (string, comma-separated)<br/>`monitoredOnly` (boolean, default true)<br/>`maxItems` (integer, default 25, max 100) | Triggers batch search dispatches for selected units, missing items, or cutoff-unmet items. |
| **18** | `queue_list` | `queue:read` | `readOnlyHint: true`<br/>`destructiveHint: false`<br/>`idempotentHint: true` | `includeTracked` (boolean, default true)<br/>`trackedLimit` (integer, default 50, max 200) | Inspects the active download queue across configured Torrent/Usenet clients plus CDH tracked download import states. |
| **19** | `queue_control` | `queue:manage` | `readOnlyHint: false`<br/>`destructiveHint: false`<br/>`idempotentHint: false` | `action` (string, required: `pause`\|`resume`\|`remove`\|`sweep`\|`process_completed`\|`retry_tracked_import`\|`ignore_tracked`)<br/>`clientId` (integer, for `pause`\|`resume`\|`remove`)<br/>`externalId` (string, for `pause`\|`resume`\|`remove`)<br/>`removeFromClient` (boolean, default true for `remove`)<br/>`deleteData` (boolean, default false for `remove`)<br/>`blocklist` (boolean, default false for `remove`)<br/>`searchReplacement` (boolean, default false for `remove`)<br/>`title` (string, optional)<br/>`infoHash` (string, optional)<br/>`trackedId` (integer, for `retry_tracked_import`\|`ignore_tracked`)<br/>`strikeThreshold` (integer, default 3 for `sweep`) | Controls queued or tracked downloads: pause/resume/remove (with optional blocklist & replacement search), sweep stalled downloads, process completed downloads immediately, or retry/ignore tracked CDH imports. |
| **20** | `manual_import_preview` | `queue:read` | `readOnlyHint: true`<br/>`destructiveHint: false`<br/>`idempotentHint: true` | `folder` (string, required)<br/>`vertical` (string, optional)<br/>`workId` (integer, optional) | Scans a completed download folder to preview file matches against catalog works and episodes prior to manual import. |
| **21** | `manual_import_execute` | `queue:manage` | `readOnlyHint: false`<br/>`destructiveHint: false`<br/>`idempotentHint: false` | `items` (object array, required: `[{ sourceFilePath, workId, seasonNumber?, episodeNumbers?, unitIds?, qualityName?, languages? }]`)<br/>`importMode` (string, optional: `hardlink_or_copy`\|`move`\|`copy`) | Imports one or more mapped files from download folders into the target library root folder via hardlink/copy or move. |
| **22** | `activity_history` | `queue:read` | `readOnlyHint: true`<br/>`destructiveHint: false`<br/>`idempotentHint: true` | `page` (integer, default 1)<br/>`pageSize` (integer, default 50, max 200)<br/>`eventType` (string, optional: `grabbed`\|`imported`\|`failed`\|`quarantined`\|`deleted`)<br/>`vertical` (string, optional)<br/>`workId` (integer, optional) | Queries historical activity events across grabs, imports, failed downloads, and language verification results. |
| **23** | `activity_blocklist` | `queue:read`, `queue:manage` | `readOnlyHint: false`<br/>`destructiveHint: false`<br/>`idempotentHint: false` | `action` (string, default `list`: `list`\|`remove`\|`clear`)<br/>`q` (string, optional)<br/>`protocol` (string, optional: `torrent`\|`usenet`)<br/>`workId` (integer, optional)<br/>`limit` (integer, default 50)<br/>`offset` (integer, default 0)<br/>`infoHash` (string, optional for `remove`)<br/>`infoHashes` (string array, optional for `remove`) | Lists, filters, removes individual/batched entries from, or clears the recurring release blocklist. |
| **24** | `catalog_scan_disk` | `catalog:write` | `readOnlyHint: false`<br/>`destructiveHint: false`<br/>`idempotentHint: true` | `workId` (integer, optional)<br/>`libraryId` (integer, optional)<br/>`verifyLanguages` (boolean, default false) | Scans a single work, an entire library, or all libraries on disk for new/missing files, optionally running 3-tier audio/subtitle language verification when scanning a `workId`. |
| **25** | `quarantine_list` | `quarantine:review` | `readOnlyHint: true`<br/>`destructiveHint: false`<br/>`idempotentHint: true` | `status` (string, optional: `pending`\|`approved`\|`rejected`) | Lists quarantined media imports awaiting manual language review or resolved historically. |
| **26** | `quarantine_resolve` | `quarantine:review` | `readOnlyHint: false`<br/>`destructiveHint: false`<br/>`idempotentHint: false` | `quarantineId` (integer, required)<br/>`decision` (string, required: `approve`\|`reject`)<br/>`confirmedLanguage` (string, default `de`) | Resolves a quarantined import: approve with confirmed audio language (auto-patching MKV headers) or reject and blocklist the release. |
| **27** | `profiles_list` | `profiles:read`, `catalog:read`, `catalog:write` | `readOnlyHint: true`<br/>`destructiveHint: false`<br/>`idempotentHint: true` | *None* | Lists all configured composable quality profiles and language verification policies (with cutoff thresholds and scoring bonuses). |
| **28** | `indexers_and_downloaders_list` | `indexers:read`, `downloaders:read` | `readOnlyHint: true`<br/>`destructiveHint: false`<br/>`idempotentHint: true` | *None* | Lists all configured indexers and download clients visible to the caller's authorized scopes. |
| **29** | `filesystem_browse` | `catalog:write`, `queue:manage`, `downloaders:write`, `system:admin`, `migration:execute` | `readOnlyHint: true`<br/>`destructiveHint: false`<br/>`idempotentHint: true` | `path` (string, optional)<br/>`includeFiles` (boolean, default false) | Browses host filesystem folders allowed to Managerr (useful when choosing library paths or manual import folders). |
| **30** | `system_status_and_tasks` | `queue:read`, `system:admin`, `catalog:write` | `readOnlyHint: false`<br/>`destructiveHint: false`<br/>`idempotentHint: false` | `runTaskKey` (string, optional)<br/>`resolveHealthActionKey` (string, optional) | Inspects Managerr version, health checks, and scheduled background tasks, or triggers a task execution / resolves a health issue. |

---

### 6.3 Cleanerr Lifecycle Tools (31-40)

| # | Tool Name | Required Scope(s) | Annotations | Input Parameters | Operational Description & Behavior |
| :-: | :--- | :--- | :--- | :--- | :--- |
| **31** | `cleanerr_get_overview` | `cleanerr:read` | `readOnlyHint: true`<br/>`destructiveHint: false`<br/>`idempotentHint: true` | *None* | Returns Cleanerr storage overview, staged Leaving Soon counts, active exclusions count, enabled workflows, and Discord Gateway status. |
| **32** | `cleanerr_list_workflows` | `cleanerr:read` | `readOnlyHint: true`<br/>`destructiveHint: false`<br/>`idempotentHint: true` | *None* | Lists configured Cleanerr lifecycle workflows (both Maintainerr rule-group and visual DAG workflows) with dry-run flag, schedule, and last run status. |
| **33** | `cleanerr_simulate_workflow` | `cleanerr:read` | `readOnlyHint: true`<br/>`destructiveHint: false`<br/>`idempotentHint: true` | `workflowId` (integer, optional)<br/>`graphJson` (string/object, optional inline DAG graph)<br/>`ruleGroup` (object, optional inline Maintainerr rule group)<br/>`simulatedFreeSpaceGb` (number, optional)<br/>`simulatedFreeSpacePercent` (number, optional) | Dry-run simulates a saved workflow (`workflowId`) or an inline `graphJson` / `ruleGroup` definition (with optional disk space overrides) without staging or deleting any media. |
| **34** | `cleanerr_run_workflow` | `cleanerr:execute` | `readOnlyHint: false`<br/>`destructiveHint: true`<br/>`idempotentHint: false` | `workflowId` (integer, required)<br/>`dryRun` (boolean, optional override) | Executes a Cleanerr lifecycle workflow immediately, optionally overriding its dry-run mode for that run. |
| **35** | `cleanerr_list_queue` | `cleanerr:read` | `readOnlyHint: true`<br/>`destructiveHint: false`<br/>`idempotentHint: true` | `status` (string, optional: `staged`\|`rescued`\|`deleted`\|`excluded`\|`failed`) | Lists items in Cleanerr's Leaving Soon deletion queue, optionally filtered by status. |
| **36** | `cleanerr_rescue_queue_item` | `cleanerr:rescue` | `readOnlyHint: false`<br/>`destructiveHint: false`<br/>`idempotentHint: true` | `queueItemId` (integer, required)<br/>`reason` (string, optional) | Rescues a staged item from Cleanerr's Leaving Soon deletion queue so it will not be deleted on grace expiry. |
| **37** | `cleanerr_postpone_queue_item` | `cleanerr:rescue` | `readOnlyHint: false`<br/>`destructiveHint: false`<br/>`idempotentHint: false` | `queueItemId` (integer, required)<br/>`additionalDays` (integer, required) | Extends the grace period for a staged Leaving Soon queue item by the specified number of days. |
| **38** | `cleanerr_exclude_media` | `cleanerr:exclude` | `readOnlyHint: false`<br/>`destructiveHint: false`<br/>`idempotentHint: true` | `mediaItemIds` (integer array, required)<br/>`reason` (string, optional)<br/>`expiresAt` (string, optional ISO-8601 timestamp) | Excludes one or more Cleanerr media items from deletion (with whole-series family escalation for TV shows). |
| **39** | `cleanerr_delete_exclusion` | `cleanerr:write` | `readOnlyHint: false`<br/>`destructiveHint: true`<br/>`idempotentHint: true` | `exclusionId` (integer, required) | Removes an existing Cleanerr exclusion rule by ID. |
| **40** | `cleanerr_test_media` | `cleanerr:read` | `readOnlyHint: true`<br/>`destructiveHint: false`<br/>`idempotentHint: true` | `mediaItemId` (integer, required)<br/>`workflowId` (integer, optional)<br/>`simulatedFreeSpaceGb` (number, optional)<br/>`simulatedFreeSpacePercent` (number, optional) | Evaluates a specific Cleanerr media item against a workflow's rules and returns per-condition pass/fail traces and guard notes. |

---

## 7. Practical JSON-RPC `curl` Examples

### 7.1 Protocol Handshake (`initialize`)
```bash
curl -s -X POST "http://localhost:8989/api/v1/mcp" \
  -H "X-Api-Key: <YOUR_API_KEY>" \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "id": 1,
    "method": "initialize",
    "params": {
      "protocolVersion": "2025-06-18",
      "clientInfo": {
        "name": "ai-agent-client",
        "version": "1.0.0"
      }
    }
  }'
```

### 7.2 Tool Discovery (`tools/list`)
Query available tools (filtered by the token's authorized RBAC scopes):
```bash
curl -s -X POST "http://localhost:8989/api/v1/mcp" \
  -H "X-Api-Key: <YOUR_API_KEY>" \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "id": 2,
    "method": "tools/list",
    "params": {}
  }'
```

### 7.3 Executing a Tool Call: Filtered Catalog Search (`catalog_list_works`)
```bash
curl -s -X POST "http://localhost:8989/api/v1/mcp" \
  -H "X-Api-Key: <YOUR_API_KEY>" \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "id": 3,
    "method": "tools/call",
    "params": {
      "name": "catalog_list_works",
      "arguments": {
        "q": "Frieren",
        "vertical": "anime",
        "limit": 5
      }
    }
  }'
```

### 7.4 Executing an Interactive Release Search (`releases_interactive_search`)
```bash
curl -s -X POST "http://localhost:8989/api/v1/mcp" \
  -H "X-Api-Key: <YOUR_API_KEY>" \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "id": 4,
    "method": "tools/call",
    "params": {
      "name": "releases_interactive_search",
      "arguments": {
        "workId": 42,
        "season": 1,
        "episode": 1,
        "approvedOnly": true,
        "limit": 10
      }
    }
  }'
```

### 7.5 Batch JSON-RPC Execution
Agents can send multiple requests in a single HTTP POST request:
```bash
curl -s -X POST "http://localhost:8989/api/v1/mcp" \
  -H "X-Api-Key: <YOUR_API_KEY>" \
  -H "Content-Type: application/json" \
  -d '[
    {
      "jsonrpc": "2.0",
      "id": 10,
      "method": "tools/call",
      "params": {
        "name": "libraries_list",
        "arguments": {}
      }
    },
    {
      "jsonrpc": "2.0",
      "id": 11,
      "method": "tools/call",
      "params": {
        "name": "queue_list",
        "arguments": {}
      }
    }
  ]'
```

---

## 8. Next Steps & Related Documentation

* [REST API v1 Reference](https://github.com/m0ritz2k19/managerr/wiki/11-REST-API-v1-Reference): Full REST API endpoint reference and OpenAPI specification.
* [Authentication, RBAC & Security](./02-SECURITY-AND-API-SPEC.md): Configuring scoped API keys and permission boundaries.
* [Quality Profiles & Language Policies](./04-PROFILES-SCORING-AND-LANGUAGE-UPGRADES.md): Composable profile architectures and TRaSH custom format compilation.
