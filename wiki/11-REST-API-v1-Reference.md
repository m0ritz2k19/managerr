# REST API v1 Reference & Developer Guide

Managerr exposes a contract-first REST API mounted under `/api/v1/*`, complemented by compatibility shims under `/compat/*` and an OpenAPI 3.1 specification. The API uses token and session authentication, Role-Based Access Control (RBAC) across 36 permission scopes, RFC 7807/9457 error semantics, idempotent state mutation, and real-time event streaming.

---

## 1. Authentication & Security

All API endpoints (except public authentication handshake routes, Ed25519 Discord interactions, and HMAC-signed magic links) require authentication. Managerr supports four authentication transports:

### 1.1 Supported Authentication Methods

| Method | Transport / Header | Description | Typical Use Case |
| :--- | :--- | :--- | :--- |
| **API Key Header (Recommended)** | `X-Api-Key: <YOUR_API_KEY>` | Primary machine-to-machine authentication header. | Automation scripts, CLI tools, external daemons |
| **Bearer Token Header** | `Authorization: Bearer <YOUR_API_KEY>` | Standard HTTP Authorization header. | Modern HTTP clients, reverse proxy bridges |
| **Session Cookie** | `Cookie: managerr_session=<TOKEN>` | Encrypted, short-lived session cookie (`HttpOnly; Secure; SameSite=Strict`). | Web UI single-page application |
| **Query Parameter** | `?apikey=<YOUR_API_KEY>` | Query string token authentication. | RFC 5545 iCalendar feeds, Torznab aggregators |

### 1.2 Scoped Machine API Keys

API keys in Managerr follow the prefix format:
```text
mgr_live_<unique_key_id>_<crypto_secret>
```

* **Storage Security:** The raw secret is only displayed once upon creation. Managerr hashes the key using `HMAC-SHA256` prior to database persistence.
* **Scope-Restricted Tokens:** Keys can be bound to specific permission scopes (e.g. `catalog:read`, `indexers:search`, `cleanerr:read`), preventing compromised tokens from performing unauthorized actions.
* **Library, Network & Expiry Constraints:** API keys can enforce `libraryIds` scoping, CIDR allowlists (e.g. `192.168.1.0/24`), and automatic `expiresAt` timestamps.

---

## 2. OpenAPI 3.1 & Interactive Documentation

* **OpenAPI 3.1 JSON Specification:** Served live at `/api/v1/openapi.json`.
* **Client SDK Generation:** Suitable for OpenAPI Generator, `openapi-typescript`, and Kiota.
* **Interactive UI:** Available via the built-in Swagger/Scalar interface at `/docs` or `/swagger` when enabled in system settings.

---

## 3. Real-Time Event Streaming & SignalR

Managerr provides real-time event notifications across the media lifecycle:

* **SignalR Hub (`/api/v1/realtime`):** Native ASP.NET Core SignalR hub supporting WebSockets and Long Polling fallbacks. Clients authenticate via standard header or query token and automatically join groups based on their authorized scopes (`scope:{scope}`) and user identity (`user:{uid}`).
* **Server-Sent Events (`GET /api/v1/realtime/stream`):** Lightweight HTTP streaming connection emitting JSON events (`grab`, `import`, `upgrade`, `rename`, `delete`, `queue_update`, `health_issue`, `scan_progress`, `cleanerr.updated`).

---

## 4. Error Handling & Idempotency

### 4.1 RFC 7807 / RFC 9457 Problem Details
All 4xx and 5xx responses return structured `ProblemDetails` JSON:
```json
{
  "type": "https://managerr.local/errors/ERR_NOT_FOUND",
  "title": "Resource Not Found",
  "status": 404,
  "detail": "Media work #1024 does not exist in the active catalog.",
  "instance": "/api/v1/catalog/works/1024",
  "traceId": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
  "errors": {}
}
```

### 4.2 Idempotency Key (`Idempotency-Key`)
To prevent accidental duplicate executions across network retries (such as multiple release grabs or repeated work additions), client applications can include an `Idempotency-Key` header with a unique UUIDv4 on state-modifying requests (`POST`, `PUT`, `DELETE`):
```http
POST /api/v1/downloaders/grab HTTP/1.1
Host: localhost:8989
X-Api-Key: <YOUR_API_KEY>
Idempotency-Key: e6b5c7e1-884d-44a5-9273-51b8a514d86b
Content-Type: application/json
```

---

## 5. Complete Endpoint Reference Tables

The following reference covers every endpoint mapped across Managerr's host endpoint modules.

### 5.1 Authentication & Session Management (`AuthEndpoints.cs`)

| Method | Route | Required Scope | Query / Body Parameters | Description |
| :--- | :--- | :--- | :--- | :--- |
| `GET` | `/api/v1/auth/providers` | *Public* | None | Lists enabled authentication mechanisms (`plex`, `local`, `oidc`, `passkeys`). |
| `GET` | `/api/v1/auth/status` | *Public* | None | Returns setup status, version, and whether server ownership has been claimed. |
| `POST` | `/api/v1/auth/plex/pin` | *Public* | None | Requests a new Plex OAuth PIN (`pinId`, `code`, `authUrl`). |
| `GET` | `/api/v1/auth/plex/callback` | *Public* | `pinId` | Browser callback for Plex OAuth popups. |
| `GET` | `/api/v1/auth/plex/poll/{pinId}` | *Public* | None | Polls Plex PIN verification; returns session cookie upon sign-in. |
| `POST` | `/api/v1/auth/plex/home-switch` | *Public* | Body: `{ homeUserId, pin }` | Switches active session to a Plex Home managed household profile. |
| `POST` | `/api/v1/auth/breakglass/setup` | `system:admin` | Body: `{ password }` | Configures emergency offline local administrator password. |
| `POST` | `/api/v1/auth/breakglass/login` | *Public* | Body: `{ username, password }` | Authenticates via emergency break-glass credentials. |
| `POST` | `/api/v1/auth/local/claim-owner` | *Public* | Body: `{ username, password, email }` | Claims first-run instance ownership via local credentials. |
| `POST` | `/api/v1/auth/local/claim` | *Public* | Body: `{ username, password }` | Alternative initial owner claim endpoint. |
| `POST` | `/api/v1/auth/local/login` | *Public* | Body: `{ username, password }` | Authenticates using local username and password. |
| `PUT` | `/api/v1/auth/profile` | *Authenticated* | Body: `{ preferredAudioLang, preferredSubLang, theme }` | Updates profile preferences for current user. |
| `POST` | `/api/v1/auth/local/password` | *Authenticated* | Body: `{ currentPassword, newPassword }` | Updates local user password. |
| `GET` | `/api/v1/auth/oidc/login` | *Public* | `redirectUri` | Starts OpenID Connect login sequence. |
| `GET` | `/api/v1/auth/oidc/start` | *Public* | None | Direct redirect to OIDC identity provider. |
| `GET` | `/api/v1/auth/oidc/callback` | *Public* | `code`, `state` | Handles GET redirect callback from OIDC IdP. |
| `POST` | `/api/v1/auth/oidc/callback` | *Public* | Form: `code`, `state` | Handles POST `form_post` callback from OIDC IdP. |
| `POST` | `/api/v1/auth/passkeys/register/options`| *Authenticated* | None | Generates WebAuthn registration options. |
| `POST` | `/api/v1/auth/passkeys/register/verify` | *Public* | Body: WebAuthn attestation payload | Verifies passkey registration and registers public key. |
| `POST` | `/api/v1/auth/passkeys/login/options` | *Public* | None | Generates WebAuthn assertion challenge options. |
| `POST` | `/api/v1/auth/passkeys/login/verify` | *Public* | Body: WebAuthn assertion payload | Verifies passkey signature and issues session cookie. |
| `GET` | `/api/v1/auth/passkeys` | *Authenticated* | None | Lists registered passkeys for the current user. |
| `PUT` | `/api/v1/auth/passkeys/{credentialId}` | *Authenticated* | Body: `{ label }` | Renames a registered passkey. |
| `DELETE` | `/api/v1/auth/passkeys/{credentialId}` | *Authenticated* | None | Revokes a registered passkey. |
| `POST` | `/api/v1/auth/logout` | *Authenticated* | None | Invalidates the active session cookie or bearer token. |
| `GET` | `/api/v1/auth/me` | *Authenticated* | None | Returns identity details, assigned roles, scopes, and quotas. |
| `GET` | `/api/v1/auth/tokens` | *Authenticated* | None | Lists scoped machine API keys owned by current user. |
| `POST` | `/api/v1/auth/tokens` | *Authenticated* | Body: `{ name, scopes, libraryIds, expiresAt, sourceCidr }` | Generates a new scoped API key. |
| `DELETE` | `/api/v1/auth/tokens/{id}` | *Authenticated* | None | Revokes an API key. |

---

### 5.2 Users & Custom Roles (`UsersAndRolesEndpoints.cs`)

| Method | Route | Required Scope | Query / Body Parameters | Description |
| :--- | :--- | :--- | :--- | :--- |
| `GET` | `/api/v1/users` | `users:read` | None | Lists all synced Plex users, local users, and roles. |
| `POST` | `/api/v1/users/local` | `system:admin` | Body: `{ username, password, email, roleIds }` | Creates a local user account. |
| `POST` | `/api/v1/users/sync-plex` | `users:manage` | None | Triggers immediate sync with PMS and plex.tv friend lists. |
| `PUT` | `/api/v1/users/{id}/roles` | `users:manage` | Body: `{ roleIds: [] }` | Replaces assigned roles for a user. |
| `PATCH` | `/api/v1/users/{id}/roles` | `users:manage` | Body: `{ addRoleIds: [], removeRoleIds: [] }` | Incremental role assignment. |
| `PATCH` | `/api/v1/users/{id}` | `users:manage` | Body: `{ isEnabled, requestQuotaLimit, sharedLibraryIds }` | Updates user settings, status, quotas, and library permissions. |
| `PATCH` | `/api/v1/users/me/preferences` | `users:read` | Body: `{ preferredAudioLang, preferredSubLang, theme }` | Updates current user's preferences. |
| `GET` | `/api/v1/roles` | `users:read` | None | Lists all built-in and custom RBAC roles. |
| `GET` | `/api/v1/roles/scopes` | `users:read` | None | Returns list of all 36 valid permission scopes and descriptions. |
| `POST` | `/api/v1/roles` | `roles:manage` | Body: `{ name, description, scopes: [], allowedLibraryIds, movieRequestQuota, tvSeasonRequestQuota, musicBookRequestQuota, cleanerrExclusionLimit }` | Creates a new custom RBAC role. |
| `POST` | `/api/v1/roles/{id}/clone` | `roles:manage` | Body: `{ name, description }` | Clones an existing role into a new custom role. |
| `PUT` | `/api/v1/roles/{id}` | `roles:manage` | Body: Role definition payload | Updates custom role definitions. |
| `DELETE` | `/api/v1/roles/{id}` | `roles:manage` | None | Deletes a custom RBAC role. |

---

### 5.3 Catalog, Media Works & Metadata (`CatalogAndMetadataEndpoints.cs`)

| Method | Route | Required Scope | Query / Body Parameters | Description |
| :--- | :--- | :--- | :--- | :--- |
| `GET` | `/api/v1/libraries` | `catalog:read` | None | Lists configured media libraries. |
| `POST` | `/api/v1/libraries` | `catalog:write` | Body: `{ name, mediaType, rootFolderPath, defaultProfileId, metadataWaterfall }` | Creates a new media library. |
| `PATCH` | `/api/v1/libraries/{id}` | `catalog:write` | Body: `{ name, rootFolderPath, defaultProfileId, metadataWaterfall }` | Updates library configuration. |
| `DELETE` | `/api/v1/libraries/{id}` | `catalog:delete` | None | Deletes a media library. |
| `GET` | `/api/v1/catalog/works` | `catalog:read` | Query: `libraryId` (long), `libraryIds` (string), `vertical` (string), `verticals` (string), `q` (string), `monitored` (bool), `availability` (string), `classification` (string), `health` (string), `sort` (`title`\|`year`\|`added`\|`dateAdded`\|`addedAt`\|`rating`), `order` (`asc`\|`desc`), `limit` (int), `offset` (int) | Server-side filtered, sorted (with `added`/`dateAdded`/`addedAt` aliases), and paginated query of catalog media works. |
| `GET` | `/api/v1/catalog/works/{id}` | `catalog:read` | None | Returns complete detail tree for a media work (containers, units, files, metadata). |
| `POST` | `/api/v1/catalog/works` | `catalog:write` | Body: `{ libraryId, title, workKind, originalLanguage, year, status, overview, posterUrl, monitored, externalIds, tags, qualityProfileId, monitorMode, minimumAvailability }` | Adds a new media work to the catalog. |
| `POST` | `/api/v1/catalog/works/auto-classify` | `catalog:write` | Body: `{ title, originalLanguage, year, overview }` | Runs heuristic classifier to recommend media vertical and tags. |
| `PATCH` | `/api/v1/catalog/works/{id}` | `catalog:write` | Body: `{ title, monitored, qualityProfileId, tags, monitorMode, minimumAvailability }` | Updates work metadata, profile, or monitoring status. |
| `POST` | `/api/v1/catalog/works/{id}/refresh` | `catalog:write` | None | Forces upstream metadata refresh from TVDB/TMDB/MusicBrainz/OpenLibrary. |
| `POST` | `/api/v1/catalog/works/{id}/auto-search` | `indexers:search` | None | Executes automated search and grab for all monitored missing units in the work. |
| `POST` | `/api/v1/catalog/works/{id}/containers/{containerId}/auto-search` | `indexers:search` | None | Executes automated search for a specific container (e.g. season or album). |
| `POST` | `/api/v1/catalog/works/{id}/units/{unitId}/auto-search` | `indexers:search` | None | Executes automated search for a single unit (episode, track, or book). |
| `DELETE` | `/api/v1/catalog/works/{id}` | `catalog:delete` | Query: `deleteFiles` (bool, default `false`) | Removes a media work from the catalog (and optionally deletes files). |
| `PATCH` | `/api/v1/catalog/containers/{containerId}` | `catalog:write` | Body: `{ monitored }` | Updates container monitoring status. |
| `PATCH` | `/api/v1/catalog/units/{unitId}` | `catalog:write` | Body: `{ monitored }` | Updates unit monitoring status. |
| `POST` | `/api/v1/catalog/files` | `catalog:write` | Body: `{ mediaUnitId, relativePath, sizeBytes, mediaInfoJson }` | Manually adopts an existing on-disk media file. |
| `GET` | `/api/v1/calendar` | `catalog:read` | Query: `start` (date), `end` (date), `vertical` (string) | Returns calendar release entries across date range. |
| `GET` | `/api/v1/requests` | `catalog:read` | Query: `status`, `libraryId`, `page`, `pageSize` | Lists user media requests. |
| `POST` | `/api/v1/requests` | `catalog:request` | Body: `{ externalId, mediaType, qualityProfileId, seasons: [] }` | Submits a media request. |
| `POST` | `/api/v1/requests/{id}/decide` | `catalog:auto_approve` \| `catalog:write` | Body: `{ decision: "approve"\|"reject", reason }` | Approves or rejects a media request. |
| `GET` | `/api/v1/metadata/providers` | `catalog:read` | None | Lists upstream metadata providers and connection health. |
| `GET` | `/api/v1/metadata/providers/settings` | `system:admin` | None | Lists configured API key status and custom base URLs across all 20 metadata providers. |
| `PUT` | `/api/v1/metadata/providers/{providerKey}/settings` | `system:admin` | Body: `{ apiKey, clearApiKey, customBaseUrl, anidbClientVer, anidbTitleSearchUrl }` | Updates encrypted API key and custom base URL for a metadata provider. |
| `GET` | `/api/v1/metadata/search` | `catalog:read` | Query: `q`, `vertical`, `year` | Searches upstream metadata providers. |
| `GET` | `/api/v1/metadata/crosswalk` | `catalog:read` | Query: `sourceProvider`, `sourceId`, `targetProvider` | Crosswalks IDs (e.g. TVDB -> TMDB -> IMDB). |
| `POST` | `/api/v1/metadata/crosswalk` | `catalog:write` | Body: `{ workId, externalIds: {} }` | Manually binds external IDs to a work. |
| `POST` | `/api/v1/metadata/sidecar/parse` | `catalog:read` | Body: `{ sidecarPath }` | Parses `.nfo` or sidecar metadata. |
| `POST` | `/api/v1/metadata/sidecar/export/{workId}` | `catalog:write` | None | Writes `.nfo` metadata sidecars onto disk. |
| `GET` | `/api/v1/metadata/webhooks` | `catalog:read` | None | Lists custom HTTP metadata webhooks. |
| `POST` | `/api/v1/metadata/webhooks` | `system:admin` | Body: `{ providerKey, displayName, supportedVerticals, searchUrlTemplate, lookupUrlTemplate, apiKey }` | Registers a custom HTTP metadata webhook. |
| `DELETE` | `/api/v1/metadata/webhooks/{id}` | `system:admin` | None | Deletes a custom HTTP metadata webhook. |

---

### 5.4 Media Management, Naming & Disk Scanning (`MediaManagementEndpoints.cs`)

| Method | Route | Required Scope | Query / Body Parameters | Description |
| :--- | :--- | :--- | :--- | :--- |
| `GET` | `/api/v1/settings/naming` | `catalog:read` | None | Retrieves file and folder renaming templates. |
| `PUT` | `/api/v1/settings/naming` | `system:admin` | Body: Renaming format templates | Updates file and folder naming templates. |
| `POST` | `/api/v1/settings/naming/preview` | `catalog:read` | Body: `{ pattern, workId, sampleReleaseTitle }` | Previews formatted path for a title. |
| `POST` | `/api/v1/libraries/{id}/scan` | `catalog:write` | None | Initiates disk scan for a library to detect new or moved files. |
| `GET` | `/api/v1/libraries/{id}/rename-preview` | `catalog:read` | None | Previews rename operations across an entire library. |
| `POST` | `/api/v1/libraries/{id}/rename-execute` | `catalog:write` | None | Executes disk renaming across an entire library. |
| `GET` | `/api/v1/libraries/unmapped-files` | `catalog:read` | None | Lists orphaned files on disk not associated with any catalog work. |
| `POST` | `/api/v1/libraries/unmapped-files/map` | `catalog:write` | Body: `{ unmappedFileId, mediaUnitId }` | Maps an orphaned file to an existing media unit. |
| `DELETE` | `/api/v1/libraries/unmapped-files/{id}` | `catalog:delete` | None | Deletes an unmapped file from disk. |
| `POST` | `/api/v1/catalog/works/{id}/scan` | `catalog:write` | None | Rescans disk directory for a single work. |
| `POST` | `/api/v1/catalog/works/{id}/scan-and-verify` | `catalog:write` | None | Rescans work folder and triggers audio/subtitle language verification. |
| `POST` | `/api/v1/catalog/works/bulk-scan-and-verify` | `catalog:write` | Body: `{ workIds: [] }` | Bulk triggers disk scan and language verification. |
| `GET` | `/api/v1/catalog/works/{id}/rename-preview` | `catalog:read` | None | Previews rename changes for a single media work. |
| `POST` | `/api/v1/catalog/works/{id}/rename-execute` | `catalog:write` | None | Executes disk rename for files in a single media work. |
| `POST` | `/api/v1/catalog/works/{id}/monitor-preset` | `catalog:write` | Body: `{ preset: "all"\|"future"\|"missing"\|"none" }` | Applies monitoring preset to work containers and units. |
| `POST` | `/api/v1/catalog/works/bulk-edit` | `catalog:write` | Body: `{ workIds: [], qualityProfileId, monitored, libraryId }` | Bulk updates settings across multiple works. |
| `GET` | `/api/v1/realtime/stream` | `catalog:read` | None | Server-Sent Events (SSE) stream for real-time progress events. |

---

### 5.5 Profiles, Quality & Language Verification (`ProfilesAndVerificationEndpoints.cs`)

| Method | Route | Required Scope | Query / Body Parameters | Description |
| :--- | :--- | :--- | :--- | :--- |
| `GET` | `/api/v1/profiles` | `profiles:read` | None | Lists all composable quality profiles. |
| `POST` | `/api/v1/profiles` | `profiles:write` | Body: `{ name, mediaVertical, items: [], cutoff, upgradeAllowed, weightEfficiency, minTraitScore, mixinIds, retainAudioLangs, retainSubLangs }` | Creates a new composable quality profile. |
| `GET` | `/api/v1/profiles/{id}` | `profiles:read` | None | Returns detailed quality profile configuration. |
| `PUT` | `/api/v1/profiles/{id}` | `profiles:write` | Body: Quality profile payload | Updates a quality profile. |
| `DELETE` | `/api/v1/profiles/{id}` | `profiles:write` | None | Deletes a quality profile. |
| `GET` | `/api/v1/profiles/mixins` | `profiles:read` | None | Lists reusable trait scoring mixins. |
| `POST` | `/api/v1/profiles/mixins` | `profiles:write` | Body: `{ name, description, traitRules: [] }` | Creates a reusable trait mixin. |
| `PUT` | `/api/v1/profiles/mixins/{id}` | `profiles:write` | Body: `{ name, description, traitRules: [] }` | Updates an existing trait mixin. |
| `DELETE` | `/api/v1/profiles/mixins/{id}` | `profiles:write` | None | Deletes a trait mixin. |
| `GET` | `/api/v1/profiles/custom-formats` | `profiles:read` | None | Lists Custom Formats (CFs) and trait scoring rules. |
| `POST` | `/api/v1/profiles/custom-formats` | `profiles:write` | Body: `{ name, specifications: [] }` | Creates a Custom Format. |
| `PUT` | `/api/v1/profiles/custom-formats/{id}` | `profiles:write` | Body: `{ name, specifications: [] }` | Updates a Custom Format. |
| `DELETE` | `/api/v1/profiles/custom-formats/{id}` | `profiles:write` | None | Deletes a Custom Format. |
| `POST` | `/api/v1/profiles/import-trash` | `profiles:write` | Body: `{ rawJson }` | Imports TRaSH Guides / Recyclarr Custom Format JSON definitions. |
| `GET` | `/api/v1/profiles/language-policies` | `profiles:read` | None | Lists language policies. |
| `POST` | `/api/v1/profiles/language-policies` | `profiles:write` | Body: `{ name, primaryLanguages: [], audioRequirement, subtitleRequirement, allowLanguageUpgrades }` | Creates a language policy. |
| `PUT` | `/api/v1/profiles/language-policies/{id}` | `profiles:write` | Body: Language policy payload | Updates a language policy. |
| `DELETE` | `/api/v1/profiles/language-policies/{id}` | `profiles:write` | None | Deletes a language policy. |
| `GET` | `/api/v1/verification/settings` | `langverify:read` \| `profiles:read` | None | Retrieves configurable language verification thresholds and `defaultProfileSlug`. |
| `PUT` | `/api/v1/verification/settings` | `profiles:write` \| `langverify:execute` \| `system:admin` | Body: `{ autoPassConfidence, quarantineMinConfidence, autoPatchHeaderConfidence, fakeDubCorrelationThreshold, defaultProfileSlug }` | Updates language verification thresholds and fallback default profile slug. |
| `GET` | `/api/v1/verification/reports` | `langverify:read` | Query: `limit` (int, default `50`) | Lists media file language verification reports. |
| `POST` | `/api/v1/verification/scan` | `langverify:execute`| Body: `RunVerificationRequest` (`{ stagedFilePath, mediaVertical, releaseTitle, expectedAudioLanguages, ... }`) | Runs 3-tier language verification scan on a media file. |
| `GET` | `/api/v1/quarantine` | `quarantine:review` | Query: `status` (`pending`\|`approved`\|`rejected`) | Lists media files quarantined due to language or codec failures. |
| `POST` | `/api/v1/quarantine/{id}/approve` | `quarantine:review` | Body: `{ confirmedLanguage }` | Approves quarantined import and auto-patches MKV language headers. |
| `POST` | `/api/v1/quarantine/{id}/reject` | `quarantine:review` | None | Rejects quarantined import, blocklists hash, and searches replacement. |
| `GET` | `/api/v1/quarantine/{id}/snippets/{index}`| `quarantine:review`| None | Streams 10-second Opus speech snippet for quarantined import. |

---

### 5.6 Acquisition: Indexers, Downloaders & Parsing (`AcquisitionEndpoints.cs`)

| Method | Route | Required Scope | Query / Body Parameters | Description |
| :--- | :--- | :--- | :--- | :--- |
| `GET` | `/api/v1/indexers/definitions` | `indexers:read` | None | Lists built-in Cardigann/Torznab indexer definition templates. |
| `GET` | `/api/v1/indexers` | `indexers:read` | None | Lists configured indexers, health status, and response metrics. |
| `POST` | `/api/v1/indexers` | `indexers:write` | Body: `{ name, implementation, protocol, baseUrl, apiKey, categories: [], requestDelayMs, tags: [] }` | Configures a new indexer. |
| `PUT` | `/api/v1/indexers/{id}` | `indexers:write` | Body: `{ name, baseUrl, apiKey, categories: [], requestDelayMs, tags: [] }` | Updates indexer settings. |
| `POST` | `/api/v1/indexers/test` | `indexers:write` | Body: Draft indexer configuration | Tests connectivity and queries indexer capabilities (`t=caps`). |
| `DELETE` | `/api/v1/indexers/{id}` | `indexers:write` | None | Removes an indexer. |
| `GET` | `/api/v1/indexers/proxies` | `indexers:read` | None | Lists configured indexer proxies (FlareSolverr, Byparr, HTTP, SOCKS4/5). |
| `POST` | `/api/v1/indexers/proxies` | `indexers:write` | Body: `{ name, proxyType, host, port, tags: [] }` | Configures an indexer proxy. |
| `DELETE` | `/api/v1/indexers/proxies/{id}` | `indexers:write` | None | Deletes an indexer proxy. |
| `POST` | `/api/v1/indexers/search` | `indexers:search` | Body: `IndexerSearchQuery` (query, mediaVertical, categories, imdbId, tmdbId, tvdbId, season, episode, workId, unitId). Query: `q` (string), `protocol` (`torrent`\|`usenet`), `approvedOnly` (bool), `hideBlocklisted` (bool), `sort` (`score`\|`age`\|`size`\|`seeders`\|`title`), `order` (`asc`\|`desc`), `limit` (int) | Server-side interactive search across all enabled indexers. |
| `POST` | `/api/v1/indexers/rss-sync` | `indexers:search` | None | Triggers immediate RSS feed synchronization across indexers. |
| `POST` | `/api/v1/indexers/release/push` | `indexers:search` | Body: Pushed release candidate payload | Accepts an externally pushed release. |
| `GET` | `/api/v1/downloaders/drivers` | `downloaders:read`| None | Lists supported download clients (`qBittorrent`, `SABnzbd`, `Transmission`, etc.). |
| `GET` | `/api/v1/downloaders` | `downloaders:read`| None | Lists configured download clients. |
| `POST` | `/api/v1/downloaders` | `downloaders:write`| Body: `{ name, implementation, host, port, urlBase, priority, isEnabled, removeCompleted, removeFailed, watchFolder, username, password, categoryMappings: {} }` | Configures a download client. |
| `PUT` | `/api/v1/downloaders/{id}` | `downloaders:write`| Body: Download client settings payload | Updates download client settings. |
| `DELETE` | `/api/v1/downloaders/{id}` | `downloaders:write`| None | Removes a download client. |
| `GET` | `/api/v1/downloaders/remote-paths` | `downloaders:read`| None | Lists remote path mappings (translates download client paths to local paths). |
| `POST` | `/api/v1/downloaders/remote-paths` | `downloaders:write`| Body: `{ host, remotePath, localPath }` | Configures a remote path mapping. |
| `DELETE` | `/api/v1/downloaders/remote-paths/{id}`| `downloaders:write`| None | Deletes a remote path mapping. |
| `POST` | `/api/v1/downloaders/grab` | `indexers:search` | Body: `GrabReleaseRequest` (`{ title, protocol, mediaVertical, downloadUrlOrMagnet, infoHash?, workId?, containerId?, unitIds?, indexerId?, releaseGuid?, forceGrab? }`) | Dispatches an approved release grab to a download client. |
| `GET` | `/api/v1/downloaders/queue` | `queue:read` | None | Returns active download queue across all clients with speed, progress, ETA. |
| `POST` | `/api/v1/downloaders/queue/sweep` | `queue:manage` | None | Sweeps download queues to remove stalled or dead downloads. |
| `GET` | `/api/v1/downloaders/blocklist` | `queue:read` | Query: `q` (string), `protocol` (string), `workId` (long), `limit` (int), `offset` (int) | Server-side filtered query of blocklisted release infohashes. |
| `POST` | `/api/v1/downloaders/process-completed`| `queue:manage` | None | Manually triggers import post-processing on completed downloads. |
| `GET`, `POST` | `/api/v1/downloaders/notify` | `queue:manage` | Query: `apikey` (optional) | Instant completion webhook triggering completed download import. |
| `GET` | `/api/v1/downloaders/imports` | `queue:read` | None | Lists recent completed download import reports. |
| `GET` | `/api/v1/downloaders/tracked` | `queue:read` | None | Lists items currently tracked across download clients. |
| `POST` | `/api/v1/downloaders/tracked/{id}/retry-import`| `queue:manage`| None | Retries post-processing for a failed download import. |
| `POST` | `/api/v1/downloaders/tracked/{id}/ignore` | `queue:manage` | None | Marks a tracked download as ignored. |
| `POST` | `/api/v1/parser/parse` | `catalog:read` | Body: `{ title }` | Parses a release title into quality, codec, language, and group metadata. |

---

### 5.7 Wanted, Activity, Queue & Blocklist (`WantedAndActivityEndpoints.cs`)

| Method | Route | Required Scope | Query / Body Parameters | Description |
| :--- | :--- | :--- | :--- | :--- |
| `GET` | `/api/v1/wanted/missing` | `catalog:read` | Query: `vertical`, `libraryId`, `page`, `pageSize` | Lists monitored catalog units currently missing media files. |
| `GET` | `/api/v1/wanted/cutoff-unmet` | `catalog:read` | Query: `vertical`, `libraryId`, `page`, `pageSize` | Lists units that have files but have not met quality profile cutoff. |
| `POST` | `/api/v1/wanted/search` | `indexers:search` | Body: `{ mode: "missing"\|"cutoff"\|"selected", unitIds: [] }` | Triggers bulk automated release searches for wanted media. |
| `GET` | `/api/v1/catalog/works/{id}/releases` | `indexers:search` | Query: `containerId` (long), `unitId` (long), `season` (int), `episode` (int), `q` (string), `protocol` (`torrent`\|`usenet`), `approvedOnly` (bool), `hideBlocklisted` (bool), `sort` (`score`\|`age`\|`size`\|`seeders`\|`title`), `order` (`asc`\|`desc`), `limit` (int) | Scoped interactive release search for a specific media work. |
| `POST` | `/api/v1/downloaders/queue/{clientId}/{externalId}/control` | `queue:manage` | Body: `{ action: "pause"\|"resume"\|"delete" }` | Controls an active download item in a client's queue. |
| `DELETE` | `/api/v1/downloaders/queue/{clientId}/{externalId}` | `queue:manage` | Query: `deleteData` (bool, default `false`) | Removes an item from the download queue and optionally deletes data. |
| `GET` | `/api/v1/downloaders/manual-import` | `queue:read` | Query: `folderPath`, `workId` | Scans a folder to preview unmapped files for manual import. |
| `POST` | `/api/v1/downloaders/manual-import` | `queue:manage` | Body: `{ files: [{ path, workId, unitId, qualityName, languages }] }` | Executes manual import of selected files. |
| `GET` | `/api/v1/activity/history` | `queue:read` | Query: `page`, `pageSize`, `eventType`, `workId` | Paginated audit log of grabs, imports, upgrades, and renames. |
| `POST` | `/api/v1/activity/history/{id}/mark-failed` | `queue:manage` | None | Marks an import as failed and adds its release infohash to the blocklist. |
| `GET` | `/api/v1/activity/blocklist` | `queue:read` | Query: `q` (string matching title/reason/hash), `protocol` (string), `workId` (long), `limit` (int), `offset` (int) | Server-side filtered query of blocklisted releases. |
| `DELETE` | `/api/v1/activity/blocklist/{**infoHash}` | `queue:manage` | None | Removes a specific infohash from the recurring blocklist. |
| `POST` | `/api/v1/activity/blocklist/bulk-delete` | `queue:manage` | Body: `{ infoHashes: [] }` | Bulk unblocks release hashes. |

---

### 5.8 Notifications, Import Lists & Calendar (`EcosystemEndpoints.cs`)

| Method | Route | Required Scope | Query / Body Parameters | Description |
| :--- | :--- | :--- | :--- | :--- |
| `GET` | `/api/v1/notifications` | `catalog:read` | None | Lists configured notification connections. |
| `POST` | `/api/v1/notifications` | `catalog:write` \| `system:admin` | Body: `{ name, providerType, settings: {}, onGrab, onImport, onUpgrade, onRename, onDelete, onHealthIssue, onApplicationUpdate, tags: [], isEnabled }` | Creates a notification connection (Plex, Webhook, Discord, etc.). |
| `PUT` | `/api/v1/notifications/{id}` | `catalog:write` \| `system:admin` | Body: Notification connection payload | Updates a notification connection. |
| `DELETE` | `/api/v1/notifications/{id}` | `catalog:write` \| `system:admin` | None | Deletes a notification connection. |
| `POST` | `/api/v1/notifications/test` | `catalog:write` \| `system:admin` | Body: Notification connection payload | Tests a draft notification connection. |
| `POST` | `/api/v1/notifications/{id}/test` | `catalog:write` \| `system:admin` | None | Tests an existing notification connection. |
| `GET` | `/api/v1/import-lists` | `catalog:read` | None | Lists automated import lists. |
| `POST` | `/api/v1/import-lists` | `catalog:write` \| `system:admin` | Body: `{ name, listType, mediaVertical, libraryId, qualityProfileId, monitorMode, minimumAvailability, searchOnAdd, settings: {}, tags: [], syncIntervalHours, limit, isEnabled }` | Creates an automated import list (TMDB, Trakt, StevenLu, AniList, Custom JSON). |
| `PUT` | `/api/v1/import-lists/{id}` | `catalog:write` \| `system:admin` | Body: Import list payload | Updates an import list. |
| `DELETE` | `/api/v1/import-lists/{id}` | `catalog:write` \| `system:admin` | None | Deletes an import list. |
| `POST` | `/api/v1/import-lists/{id}/sync` | `catalog:write` \| `system:admin` | None | Manually triggers synchronization for an import list. |
| `POST` | `/api/v1/import-lists/sync-all` | `catalog:write` \| `system:admin` | None | Synchronizes all enabled import lists. |
| `GET` | `/api/v1/import-lists/exclusions` | `catalog:read` | None | Lists import list exclusions. |
| `POST` | `/api/v1/import-lists/exclusions` | `catalog:write` \| `system:admin` | Body: `{ externalKey, title, mediaVertical, year }` | Adds a title to import list exclusions. |
| `DELETE` | `/api/v1/import-lists/exclusions/{id}` | `catalog:write` \| `system:admin` | None | Removes a title from import list exclusions. |
| `GET` | `/feed/v1/calendar/managerr.ics` | `catalog:read` \| `compat:*` | Query: `pastDays` (int), `futureDays` (int), `vertical` (string), `unmonitored` (bool), `tags` (string) | Returns RFC 5545 iCalendar stream. |
| `GET` | `/api/v1/calendar/managerr.ics` | `catalog:read` \| `compat:*` | Query: Same as `/feed/v1/calendar/managerr.ics` | Alternative iCalendar subscription URL. |

---

### 5.9 Migration & Compatibility Shims (`MigrationAndCompatEndpoints.cs`)

| Method | Route | Required Scope | Query / Body Parameters | Description |
| :--- | :--- | :--- | :--- | :--- |
| `GET` | `/api/v1/compat/settings` | `migration:read` \| `system:admin` | None | Retrieves compatibility shim settings (`expandSingleSeasonAnime`). |
| `PUT` | `/api/v1/compat/settings` | `migration:execute` \| `system:admin` | Body: `{ expandSingleSeasonAnime }` | Updates `compat.sonarr.expand_single_season_anime` toggle. |
| `GET` | `/api/v1/migration/sources` | `migration:read` | None | Lists configured migration sources. |
| `POST` | `/api/v1/migration/sources` | `migration:execute` | Body: `{ name, sourceKind, fileOrUrl, transportMode, apiKey, readarrSlotHint, pathMappings: {}, isEnabled }` | Registers a migration source. |
| `PUT` | `/api/v1/migration/sources/{id}` | `migration:execute` | Body: Migration source payload | Updates a migration source. |
| `DELETE` | `/api/v1/migration/sources/{id}` | `migration:execute` | None | Deletes a migration source. |
| `POST` | `/api/v1/migration/arr-api/probe` | `migration:read` | Body: `{ baseUrl, apiKey, sourceKind, pathMappings: {} }` | Probes live running `*arr` instance and counts items. |
| `POST` | `/api/v1/migration/arr-api/import` | `migration:execute` | Body: `{ baseUrl, apiKey, sourceKind, name, readarrSlotHint, pathMappings: {}, dryRun, saveAsSource, upliftLanguageCustomFormats }` | Ingests data directly from a live `*arr` instance via API. |
| `POST` | `/api/v1/migration/plan` | `migration:read` | Body: `{ sources: [], globalPathMappings: {}, upliftLanguageCustomFormats }` | Generates a dry-run migration plan. |
| `POST` | `/api/v1/migration/execute` | `migration:execute` | Body: Migration execution payload | Executes deterministic migration across all sources. |
| `GET` | `/api/v1/migration/runs` | `migration:read` | Query: `limit` (int) | Lists historical migration execution run reports. |
| `POST` | `/api/v1/migration/rollback` | `migration:execute` | Body: `{ removeSavedSources }` | Safely reverts migration while preserving on-disk files. |
| `GET` | `/api/v1/migration/shadow-decisions` | `migration:read` | Query: `limit` (int) | Lists release grab decisions evaluated in Shadow Mode. |
| `POST` | `/api/v1/migration/shadow-decisions/evaluate` | `migration:execute` | Body: `{ mediaVertical, workTitle, releaseTitle, profileId, legacyApp, legacyDecision }` | Evaluates a release candidate in Shadow Mode. |
| `POST` | `/api/v1/migration/cutover` | `migration:execute` | Body: `{ runFinalDeltaSync, generateScopedCompatKeys, generateSeparateReadarrAudiobookKey, globalPathMappings: {} }` | Performs final delta sync and generates scoped `/compat/*` keys. |
| `*` | `/compat/sonarr/api/v3/*` | `compat:sonarr` | See Sonarr v3 API spec | Virtual Sonarr v3 compatibility shim. |
| `*` | `/compat/radarr/api/v3/*` | `compat:radarr` | See Radarr v3 API spec | Virtual Radarr v3 compatibility shim. |
| `*` | `/compat/lidarr/api/v1/*` | `compat:lidarr` | See Lidarr v1 API spec | Virtual Lidarr v1 compatibility shim. |
| `*` | `/compat/readarr/api/v1/*` | `compat:readarr` | See Readarr v1 API spec | Virtual Readarr v1 compatibility shim. |
| `*` | `/compat/prowlarr/api/v1/*` | `compat:prowlarr` | See Prowlarr v1 API spec | Virtual Prowlarr v1 compatibility shim. |
| `GET` | `/api/v1/torznab/{indexerId}/api` | `compat:prowlarr` | Query: `t` (`caps`\|`search`\|`tvsearch`\|`movie`), `q`, `cat` | Torznab/Newznab indexer XML feed. |
| `GET` | `/compat/prowlarr/{indexerId}/api`| `compat:prowlarr` | Query: Same as Torznab | Torznab feed mounted under Prowlarr shim. |

---

### 5.10 Cleanerr Lifecycle Studio & Discord Bot (`CleanerrEndpoints.cs`)

| Method | Route | Required Scope | Query / Body Parameters | Description |
| :--- | :--- | :--- | :--- | :--- |
| `GET` | `/api/v1/cleanerr/status` | `cleanerr:read` | None | Returns Cleanerr RBAC capabilities, exclusion quota, staged count, and Discord Gateway connection status. |
| `GET` | `/api/v1/cleanerr/stats` | `cleanerr:read` | None | Returns overview storage telemetry, reclaim forecast buckets, disk free space, and queue counts. |
| `GET` | `/api/v1/cleanerr/templates` | `cleanerr:read` | None | Lists built-in DAG and Maintainerr rule templates. |
| `GET` | `/api/v1/cleanerr/rules/fields` | `cleanerr:read` | None | Lists all Maintainerr-compatible rule fields, 21 comparators, and action modes. |
| `GET` | `/api/v1/cleanerr/node-catalog` | `cleanerr:read` | None | Lists all visual DAG node definitions and default configs. |
| `POST` | `/api/v1/cleanerr/rules/test-media` | `cleanerr:read` | Body: `{ mediaItemId, ruleGroup?, graphJson?, workflowId?, simulatedFreeSpaceGb?, simulatedFreeSpacePercent? }` | Evaluates a single media item against a rule group or DAG with per-condition trace logs. |
| `GET` | `/api/v1/cleanerr/media` | `cleanerr:read` | Query: `search`, `q`, `mediaType`, `libraryId`, `page`, `pageSize`, `watched`, `sort` | Queries Cleanerr media inventory with watch history, tags, hardlink, and Seerr fields. |
| `POST` | `/api/v1/cleanerr/media` | `cleanerr:write` | Body: `UpsertCleanerrMediaItemRequest` | Upserts a Cleanerr media metadata record. |
| `POST` | `/api/v1/cleanerr/media/sync-catalog` | `cleanerr:write` | None | Syncs Managerr catalog works and file telemetry into Cleanerr. |
| `POST` | `/api/v1/cleanerr/sync-plex` | `cleanerr:write` | Body (optional): `{ plexServerUrl, plexToken }` | Syncs Plex watch state, active sessions, labels, and watchlists into Cleanerr. |
| `GET` | `/api/v1/cleanerr/media/{id}/children` | `cleanerr:read` | None | Returns child seasons and episodes for a series item. |
| `GET` | `/api/v1/cleanerr/workflows` | `cleanerr:read` | None | Lists all saved Cleanerr workflows. |
| `GET` | `/api/v1/cleanerr/workflows/{id}` | `cleanerr:read` | None | Returns a single Cleanerr workflow by ID. |
| `GET` | `/api/v1/cleanerr/workflows/{id}/yaml` | `cleanerr:read` | None | Exports a workflow or rule group as portable YAML. |
| `POST` | `/api/v1/cleanerr/workflows/import-yaml` | `cleanerr:write` | Body: `{ yaml, dryRun?, enabled?, confirmWorkflowName? }` | Imports a Maintainerr-compatible YAML workflow (requires `confirmWorkflowName` if `dryRun=false`). |
| `POST` | `/api/v1/cleanerr/workflows/{id}/duplicate` | `cleanerr:write` | Body (optional): `{ name }` | Duplicates an existing workflow. |
| `POST` | `/api/v1/cleanerr/workflows/{id}/test-media` | `cleanerr:read` | Body: `TestCleanerrMediaRequest` | Runs single-item diagnostic evaluation against workflow `{id}`. |
| `POST` | `/api/v1/cleanerr/workflows/preview-live-impact` | `cleanerr:read` | Body: `{ graphJson?, ruleGroup?, workflowName?, simulatedFreeSpaceGb?, simulatedFreeSpacePercent? }` | Computes live-impact preview (item count, GB, unwatched/requester counts) for unsaved changes. |
| `POST` | `/api/v1/cleanerr/workflows/{id}/preview-live-impact` | `cleanerr:read` | Body: `PreviewCleanerrLiveImpactRequest` | Computes live-impact preview for saved workflow `{id}`. |
| `GET` | `/api/v1/cleanerr/workflows/{id}/live-impact` | `cleanerr:read` | None | Returns live-impact preview summary for workflow `{id}`. |
| `POST` | `/api/v1/cleanerr/workflows` | `cleanerr:write` | Body: `{ name, description, enabled, dryRun, scheduleCron, graphJson, editorMode, ruleGroup, confirmWorkflowName? }` | Creates a workflow (enforces `confirmWorkflowName` confirmation gate when `dryRun=false`). |
| `PUT` | `/api/v1/cleanerr/workflows/{id}` | `cleanerr:write` | Body: `UpsertCleanerrWorkflowRequest` | Updates a workflow (enforces `confirmWorkflowName` when switching to live mode or enabling). |
| `DELETE` | `/api/v1/cleanerr/workflows/{id}` | `cleanerr:write` | None | Deletes a Cleanerr workflow. |
| `POST` | `/api/v1/cleanerr/workflows/simulate` | `cleanerr:read` | Body: `{ graphJson?, workflowId?, simulatedFreeSpaceGb?, simulatedFreeSpacePercent?, ruleGroup? }` | Dry-run simulates a draft or saved workflow without staging or deleting media. |
| `POST` | `/api/v1/cleanerr/workflows/{id}/simulate` | `cleanerr:read` | Body (optional): `{ simulatedFreeSpaceGb?, simulatedFreeSpacePercent? }` | Dry-run simulates saved workflow `{id}`. |
| `POST` | `/api/v1/cleanerr/workflows/{id}/execute` | `cleanerr:execute` | Body (optional): `{ dryRun?, simulatedFreeSpaceGb?, simulatedFreeSpacePercent? }` | Executes workflow `{id}` (records run in audit log). |
| `POST` | `/api/v1/cleanerr/workflows/{id}/run` | `cleanerr:execute` | Body (optional): `ExecuteCleanerrWorkflowHttpRequest` | Alias for `/workflows/{id}/execute`. |
| `GET` | `/api/v1/cleanerr/runs` | `cleanerr:read` | Query: `workflowId` (long), `limit` (int, default `25`) | Lists historical Cleanerr workflow execution logs. |
| `GET` | `/api/v1/cleanerr/queue` | `cleanerr:read` | Query: `status` (`staged`\|`rescued`\|`deleted`\|`excluded`\|`failed`) | Lists Leaving Soon deletion queue items. |
| `POST` | `/api/v1/cleanerr/queue/manual-stage` | `cleanerr:write` | Body: `{ mediaItemIds: [], graceDays, actionMode, reason? }` | Manually stages selected media items into the Leaving Soon queue. |
| `POST` | `/api/v1/cleanerr/queue/process` | `cleanerr:execute` | Body (optional): `{ referenceNow?, simulateDaysAhead? }` | Processes expired grace timers, auto-rescues, and final warnings. |
| `POST` | `/api/v1/cleanerr/queue/exclude-all` | `cleanerr:exclude` | Body (optional): `{ queueIds?, reason? }` | Excludes all or specified staged queue items (respects role exclusion quota). |
| `POST` | `/api/v1/cleanerr/queue/postpone-all` | `cleanerr:rescue` | Body (optional): `{ days?, reason?, workflowId? }` | Postpones deletion deadlines across all staged items. |
| `POST` | `/api/v1/cleanerr/queue/{id}/postpone` | `cleanerr:rescue` | Body (optional): `{ days?, reason? }` | Extends grace period for a staged queue item (1-365 days). |
| `POST` | `/api/v1/cleanerr/queue/{id}/rescue` | `cleanerr:rescue` | Body (optional): `{ reason? }` | Rescues an item out of the Leaving Soon queue. |
| `POST` | `/api/v1/cleanerr/queue/{id}/exclude` | `cleanerr:exclude` | Body (optional): `{ reason?, expiresAt? }` | Excludes a queued item from deletion (with whole-series escalation). |
| `POST` | `/api/v1/cleanerr/queue/{id}/delete-now` | `cleanerr:execute` | None | Immediately executes deletion action for a staged queue item and audits the event. |
| `DELETE` | `/api/v1/cleanerr/queue/{id}` | `cleanerr:write` | None | Removes a record from the deletion queue table. |
| `GET` | `/api/v1/cleanerr/exclusions` | `cleanerr:read` | None | Lists all Cleanerr exclusion rules. |
| `POST` | `/api/v1/cleanerr/exclusions` | `cleanerr:exclude` | Body: `{ mediaItemId?, externalId?, ruleType, value?, granularity?, workflowId?, reason?, expiresAt? }` | Creates an exclusion rule (`media_item`, `tag`, `plex_label`, `plex_user`, `seerr_user`, `library`). |
| `POST` | `/api/v1/cleanerr/exclusions/bulk` | `cleanerr:exclude` | Body: `{ mediaItemIds: [], reason?, expiresAt?, workflowId? }` | Bulk-excludes media items from simulation previews or media lists. |
| `DELETE` | `/api/v1/cleanerr/exclusions/{id}` | `cleanerr:write` | None | Deletes an exclusion rule. |
| `GET` | `/api/v1/cleanerr/settings` | `cleanerr:read` | None | Retrieves Cleanerr general settings (`defaultGraceDays`, `defaultPostponeDays`, `finalWarningHours`, `torrentDownloadsPath`, `seerrUrl`, `hasSeerrApiKey`). |
| `PUT` | `/api/v1/cleanerr/settings` | `cleanerr:write` | Body: `{ defaultGraceDays?, defaultPostponeDays?, finalWarningHours?, torrentDownloadsPath?, seerrUrl?, seerrApiKey? }` | Updates Cleanerr general settings. |
| `GET` | `/api/v1/cleanerr/discord/settings` | `cleanerr:read` | None | Retrieves Discord Gateway, Ed25519, webhook, `webhookBotUsername`, `authMode`, and notification toggle settings. |
| `PUT` | `/api/v1/cleanerr/discord/settings` | `cleanerr:write` | Body: `UpdateCleanerrDiscordSettingsRequest` | Updates Discord integration settings. |
| `GET` | `/api/v1/cleanerr/discord/discover` | `cleanerr:write` | None | Uses the configured Bot Token to discover Discord guilds and text channels. |
| `POST` | `/api/v1/cleanerr/discord/reconnect` | `cleanerr:write` | None | Requests an immediate Discord Gateway WebSocket reconnect. |
| `POST` | `/api/v1/cleanerr/discord/sync-commands` | `cleanerr:write` | None | Registers `/cleanerr` slash commands with Discord (`status`, `leaving-soon`, `why`, `exclude`, `postpone`, `exclusions`, `run`). |
| `POST` | `/api/v1/cleanerr/discord/test` | `cleanerr:write` | None | Sends a test notification embed to the configured Discord channel/webhook. |
| `POST` | `/api/v1/cleanerr/webhooks/discord-interactions` | *Ed25519 Signature* | `X-Signature-Ed25519`, `X-Signature-Timestamp`, JSON interaction payload | Receives Discord HTTP Interactions (slash commands, buttons, select menus). |
| `GET` | `/api/v1/cleanerr/webhooks/magic/exclude` | *HMAC Signature* | Query: `media_id`, `sig` | Single-use expiring magic link to exclude a staged item. |
| `GET` | `/api/v1/cleanerr/webhooks/magic/postpone` | *HMAC Signature* | Query: `media_id`, `days`, `sig` | Single-use expiring magic link to postpone a staged item. |
| `GET` | `/api/v1/cleanerr/webhooks/magic/rescue` | *HMAC Signature* | Query: `media_id`, `sig` | Single-use expiring magic link to rescue a staged item. |
| `GET` | `/api/v1/cleanerr/webhooks/magic/exclude-all` | *HMAC Signature* | Query: `workflow_id`, `sig` | Single-use expiring magic link to exclude all staged items in a list. |
| `POST` | `/api/v1/cleanerr/webhooks/{source}` | `cleanerr:execute` | JSON or Plex multipart `payload` form field | Inbound Plex/Arr/Seerr webhook triggering playback auto-rescue or webhook workflows. |

---

### 5.11 Filesystem Navigation (`FilesystemEndpoints.cs`)

| Method | Route | Required Scope | Query / Body Parameters | Description |
| :--- | :--- | :--- | :--- | :--- |
| `GET` | `/api/v1/filesystem` | `catalog:write` \| `queue:manage` \| `system:admin` | Query: `path` (string), `showFiles` (bool), `filter` (string) | Browses host filesystem folders and media files. |
| `POST` | `/api/v1/filesystem/directories` | `system:admin` | Body: `{ path }` | Creates a new directory on the host filesystem. |
| `GET` | `/compat/sonarr/api/v3/filesystem` | `compat:sonarr` | Query: `path` | Sonarr filesystem browser shim. |
| `GET` | `/compat/radarr/api/v3/filesystem` | `compat:radarr` | Query: `path` | Radarr filesystem browser shim. |

---

### 5.12 System Operations, Health & Backups (`SystemEndpoints.cs`)

| Method | Route | Required Scope | Query / Body Parameters | Description |
| :--- | :--- | :--- | :--- | :--- |
| `GET` | `/api/v1/system/health` | `queue:read` | None | Returns overall health status across all subsystems. |
| `GET` | `/api/v1/system/health/checks` | `queue:read` | None | Returns detailed results of health check probes. |
| `POST` | `/api/v1/system/health/resolve/{actionKey}` | `system:admin` | None | Resolves a health warning or triggers an automatic fix. |
| `GET` | `/api/v1/system/retention` | `system:admin` | None | Retrieves audit and log retention policies. |
| `PUT` | `/api/v1/system/retention` | `system:admin` | Body: Retention policy settings (supports `0`-day immediate purge) | Updates data retention policies. |
| `POST` | `/api/v1/system/retention/run` | `system:admin` | None | Executes retention cleanup to purge expired logs. |
| `GET` | `/api/v1/system/tasks` | `queue:read` | None | Lists all 10 scheduled background maintenance tasks. |
| `POST` | `/api/v1/system/tasks/{taskKey}/run` | `queue:manage` | None | Manually triggers execution of a scheduled background task. |
| `PATCH` | `/api/v1/system/tasks/{taskKey}` | `system:admin` | Body: `{ intervalSeconds?, isEnabled? }` | Updates a scheduled task's interval and enabled toggle. |
| `GET` | `/api/v1/system/metrics` | `metrics:read` | None | Telemetry metrics in Prometheus format. |
| `GET` | `/api/v1/system/storage` | `system:admin` | None | Reports mount points, filesystem types, and free disk space. |
| `GET` | `/api/v1/system/backups` | `system:admin` | None | Lists available database backups. |
| `POST` | `/api/v1/system/backups` | `system:admin` | None | Generates an immediate SQLite database backup archive. |
| `GET` | `/api/v1/system/backups/settings` | `system:admin` | None | Retrieves backup rotation settings (`maxRetainedBackups`). |
| `PUT` | `/api/v1/system/backups/settings` | `system:admin` | Body: `{ maxRetainedBackups }` | Updates maximum retained backup count (`1` to `100`). |
| `GET` | `/api/v1/system/audit` | `audit:read` | Query: `limit`, `action` | Queries immutable security and administrative audit log events. |
| `GET` | `/api/v1/system/search` | `catalog:read` | Query: `q` | Universal search across media works, settings, and navigation. |
| `GET` | `/api/v1/settings/general` | `system:admin` | None | Returns general instance configuration (`instanceName`, `security` (`trustForwardedHeaders`, `trustedProxyCidrs`, `allowedHosts`), `mcp`, `rollingQuotaWindowDays`, `backupSettings`, etc.). |
| `PUT` | `/api/v1/settings/general` | `system:admin` | Body: General settings payload | Updates general instance configuration. |
| `POST` | `/api/v1/settings/general/api-key/reset`| `system:admin` | None | Resets global administrative API key. |
| `GET` | `/api/v1/settings/auth` | `system:admin` | None | Returns authentication and reverse-proxy configuration settings. |
| `PUT` | `/api/v1/settings/auth` | `system:admin` | Body: Auth settings payload (`plex`, `oidc`, `passkeys`, `reverseProxy`) | Updates authentication configuration settings. |
| `POST` | `/api/v1/settings/auth/oidc/test` | `system:admin` | Body: OIDC draft configuration | Tests OIDC identity provider discovery and connectivity. |
| `POST` | `/api/v1/settings/auth/test-oidc` | `system:admin` | Body: OIDC configuration | Alternative OIDC testing endpoint. |

---

### 5.13 Model Context Protocol (`McpEndpoints.cs`)

| Method | Route | Required Scope | Protocol Details | Description |
| :--- | :--- | :--- | :--- | :--- |
| `POST` | `/api/v1/mcp` & `/mcp` | *Authenticated* | JSON-RPC 2.0 (`initialize`, `tools/list`, `tools/call`, `ping`) | Streamable HTTP endpoint for AI agents (40 tools). Supports batch arrays. Controlled by `system.mcp.enabled`. |
| `GET` | `/api/v1/mcp` & `/mcp` | *Authenticated* | Returns `405 Method Not Allowed` with `Allow: POST, DELETE` header | Streamable MCP HTTP spec compliance check. |
| `DELETE` | `/api/v1/mcp` & `/mcp` | *Authenticated* | Returns `204 NoContent` | Closes active MCP agent session. |

---

## 6. Practical `curl` Examples

### 6.1 Querying Catalog with Server-Side Filters & Date Added Sorting
Retrieve up to 20 complete and monitored movies sorted by date added (`added` / `dateAdded` / `addedAt`) in descending order:
```bash
curl -s -X GET "http://localhost:8989/api/v1/catalog/works?vertical=movies&monitored=true&availability=complete&sort=dateAdded&order=desc&limit=20" \
  -H "X-Api-Key: <YOUR_API_KEY>" \
  -H "Accept: application/json"
```

### 6.2 Interactive Release Search with Sorting
Perform a live indexer search for 1080p torrent releases for movie work `#42`, sorted by total weighted score:
```bash
curl -s -X POST "http://localhost:8989/api/v1/indexers/search?protocol=torrent&approvedOnly=true&hideBlocklisted=true&sort=score&order=desc&limit=15" \
  -H "X-Api-Key: <YOUR_API_KEY>" \
  -H "Content-Type: application/json" \
  -d '{
    "workId": 42,
    "interactive": true
  }'
```

### 6.3 Grabbing a Release with Idempotency Key
Dispatch an approved release candidate to the download client, ensuring network retries cannot trigger duplicate grabs:
```bash
curl -s -X POST "http://localhost:8989/api/v1/downloaders/grab" \
  -H "X-Api-Key: <YOUR_API_KEY>" \
  -H "Idempotency-Key: a1b2c3d4-e5f6-7890-abcd-ef1234567890" \
  -H "Content-Type: application/json" \
  -d '{
    "title": "Example.Movie.2024.1080p.WEB-DL.H264",
    "protocol": "torrent",
    "mediaVertical": "movies",
    "downloadUrlOrMagnet": "magnet:?xt=urn:btih:d3b07384d113edec49eaa6238ad5ff00",
    "indexerId": 3,
    "releaseGuid": "magnet:?xt=urn:btih:d3b07384d113edec49eaa6238ad5ff00",
    "workId": 42,
    "unitIds": [108]
  }'
```

### 6.4 Subscribing to Real-Time Server-Sent Events (SSE)
Stream live lifecycle events (grabs, imports, upgrades, queue progress, Cleanerr events):
```bash
curl -N -X GET "http://localhost:8989/api/v1/realtime/stream" \
  -H "X-Api-Key: <YOUR_API_KEY>" \
  -H "Accept: text/event-stream"
```

---

## 7. Next Steps & Related Documentation

* [MCP Server & AI Agent Guide](12-MCP-Server-and-AI-Agent-Guide): Using Managerr's built-in 40-tool MCP server with AI coding assistants.
* [Cleanerr Lifecycle Studio & Discord Bot](13-Cleanerr-Lifecycle-Studio-and-Discord-Bot): Visual DAG workflows, Leaving Soon queue, and interactive Discord bot.
* [Authentication, RBAC & Security](02-Authentication-RBAC-and-Security): Configuration for all 36 scopes, Plex user directory, and passkeys.
* [Ecosystem & Compatibility Shims](09-Ecosystem-Arr-Compat-and-Migration): Setup guides for Overseerr, Jellyseerr, and migration tools.
