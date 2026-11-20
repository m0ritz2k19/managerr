# 10: Complete Feature Reference (`v0.2.0`)

This document provides a consolidated architectural and operational inventory of features currently shipped on Managerr (`v0.2.0`), with cross-links to the detailed repository specifications under [`docs/`](./) and the complete **GitHub Project Wiki**:

> **Full GitHub Wiki:** [https://github.com/m0ritz2k19/managerr/wiki](https://github.com/m0ritz2k19/managerr/wiki)

---

## Feature Area Matrix

| # | Domain | Highlights | Repository Spec | GitHub Wiki Page |
| :-: | :--- | :--- | :--- | :--- |
| **1** | **Deployment & Embedded Storage** | Single-container `.NET 10` + `React 19`, 3 SQLite `WAL` + `FTS5` DBs in `/config`, `/data` atomic hardlink layout, `selfhost-setup.sh`, GHCR image | [`SELF-HOSTING.md`](./SELF-HOSTING.md), [`01`](./01-UNIFIED-ARCHITECTURE-AND-CODE-REUSE.md) | [01: Getting Started & Self-Hosting](https://github.com/m0ritz2k19/managerr/wiki/01-Getting-Started-and-Self-Hosting) |
| **2** | **Authentication, RBAC & Security** | Local accounts (`PBKDF2-SHA512`), Plex OAuth + PMS gate, OIDC 1.0 + PKCE (claim/group mappings), Passkeys, 6 built-in roles + Custom Role Builder, 36 scopes, `AES-256-GCM`, SSRF guard, forwarded-header trust | [`02`](./02-SECURITY-AND-API-SPEC.md) | [02: Authentication, RBAC & Security](https://github.com/m0ritz2k19/managerr/wiki/02-Authentication-RBAC-and-Security) |
| **3** | **Polymorphic Catalog & UX** | 7 verticals, dual literary slots, separated **Type / Library / Status / Classification / Health** filters, Date Added sort, `Back` navigation filter + scroll persistence, monitored `X / Y` progress bars, auto-classifier, `iCal`, requests | [`01`](./01-UNIFIED-ARCHITECTURE-AND-CODE-REUSE.md) | [03: Catalog, Libraries & Media Management](https://github.com/m0ritz2k19/managerr/wiki/03-Catalog-Libraries-and-Media-Management) |
| **4** | **Metadata Federation & Crosswalk** | 20 built-in providers + custom HTTP webhooks, `local_sidecar` (`NFO`/`OPF`), field locking, `aired`/`dvd`/`absolute` ordering + TMDB anime alignment, BFS crosswalk, Romaji normalization, `FTS5` BM25 search | [`06`](./06-METADATA-PROVIDER-ARCHITECTURE.md) | [04: Metadata Federation & Crosswalk](https://github.com/m0ritz2k19/managerr/wiki/04-Metadata-Federation-and-Crosswalk) |
| **5** | **Indexers & Release Search** | Newznab, Torznab, in-process Cardigann v12 YAML engine, `t=caps` caching, FlareSolverr/Byparr/HTTP/SOCKS5 proxies, circuit-breaker escalation, multi-wave search, RSS sync, `/api/v1/indexers/release/push` | [`03`](./03-INDEXERS-AND-DOWNLOADERS.md) | [05: Indexers & Release Search](https://github.com/m0ritz2k19/managerr/wiki/05-Indexers-and-Release-Search) |
| **6** | **Download Clients, CDH & Import** | 12 Torrent & Usenet drivers, Remote Path Mappings UI + API, localized `GrabHistory` correlation, adaptive queue + `/notify` webhook, CDH state machine, season-pack import, hardlinks, Manual Import, strike sweep, blocklist | [`03`](./03-INDEXERS-AND-DOWNLOADERS.md) | [06: Download Clients, Import & Activity](https://github.com/m0ritz2k19/managerr/wiki/06-Download-Clients-Import-and-Activity) |
| **7** | **Quality Profiles, TRaSH & Languages** | Composable inheritance/mixins, 4-axis scoring (`Quality + Language + Traits + Efficiency`), native TRaSH/Recyclarr importer, dual `QualityCutoff` x `LanguageCutoff`, dynamic `original`, PlexUser promotion | [`04`](./04-PROFILES-SCORING-AND-LANGUAGE-UPGRADES.md) | [07: Quality Profiles, Scoring & Language Policies](https://github.com/m0ritz2k19/managerr/wiki/07-Quality-Profiles-Scoring-and-Language-Policies) |
| **8** | **Language Verification & Quarantine** | 3-tier pipeline (`ffprobe`/OPF/PDF -> `Lingua`+`Tesseract` OCR -> `FFmpeg` + `Silero VAD v5` + `Whisper.net` + Pearson fake-dub detection + `mkvpropedit`) with configurable thresholds, multi-audio rule, Opus quarantine review | [`05`](./05-LANGUAGE-VERIFICATION-PIPELINE.md) | [08: Language Verification & Quarantine](https://github.com/m0ritz2k19/managerr/wiki/08-Language-Verification-and-Quarantine) |
| **9** | **Ecosystem, Compat & Migration** | `/compat/{sonarr,radarr,lidarr,readarr,prowlarr}` + `/api/v1/torznab/{id}/api`, Seerr Plex-tag user correlation, notifications, import lists, 5-step Migration Wizard, Shadow Mode & 1-click cutover | [`08`](./08-MIGRATION-ENGINE.md) | [09: Ecosystem, Arr Compat & Migration](https://github.com/m0ritz2k19/managerr/wiki/09-Ecosystem-Arr-Compat-and-Migration) |
| **10** | **UI, Theme Studio & System Ops** | `Cmd+K`/`Ctrl+K` Command Palette, SignalR + SSE stream, Theme Studio (5 presets, 19 tokens, WCAG AA contrast, JSON import/export), Wanted views, self-healing health actions, 10 customizable scheduled tasks, `VACUUM INTO` hot backups, retention settings | [`01`](./01-UNIFIED-ARCHITECTURE-AND-CODE-REUSE.md) | [10: UI Tour, Theme Studio & System Ops](https://github.com/m0ritz2k19/managerr/wiki/10-UI-Tour-Theme-Studio-and-System-Operations) |
| **11** | **REST API v1 & Server Filtering** | OpenAPI 3.1, RFC 9457 Problem Details, `Idempotency-Key`, server-side search/filter/sort/pagination params across catalog, releases, wanted, history, and blocklist | [`02`](./02-SECURITY-AND-API-SPEC.md) | [11: REST API v1 Reference](https://github.com/m0ritz2k19/managerr/wiki/11-REST-API-v1-Reference) |
| **12** | **Built-In MCP Server for AI Agents** | Streamable HTTP (`/api/v1/mcp`, `/mcp`, togglable via `system.mcp.enabled`), JSON-RPC 2.0 single & batch, RBAC-scoped `tools/list` + `ERR_RBAC_FORBIDDEN`, 40 specialized tools (12 Catalog + 18 Operations + 10 Cleanerr) | [`09`](./09-MCP-SERVER-AND-AGENT-API.md) | [12: MCP Server & AI Agent Guide](https://github.com/m0ritz2k19/managerr/wiki/12-MCP-Server-and-AI-Agent-Guide) |
| **13** | **Cleanerr Lifecycle Studio & Discord Bot** | Visual `@xyflow/react` DAG studio & Maintainerr-compatible Rule Builder, YAML import/export, whole-series handling, native Leaving Soon queue without Plex collection mutations, hardlink protection, `/cleanerr` Discord bot + buttons + HMAC magic links | [`01`](./01-UNIFIED-ARCHITECTURE-AND-CODE-REUSE.md), [`02`](./02-SECURITY-AND-API-SPEC.md) | [13: Cleanerr Lifecycle Studio and Discord Bot](https://github.com/m0ritz2k19/managerr/wiki/13-Cleanerr-Lifecycle-Studio-and-Discord-Bot) |

---

## 1. Deployment & Embedded SQLite Architecture

* **Embedded Databases Only:** All state lives inside `/config` across three SQLite `WAL` databases (`managerr.db` core state with `FTS5` search, `cache.db` metadata/artwork cache, and `telemetry.db` append-only audit logs and metrics).
* **Unified Hardlink Layout (`/data`):** Recommended single-mount layout (`/data/torrents`, `/data/usenet`, `/data/media/{movies,tv,anime,music,ebooks,audiobooks,comics}`) enables atomic instant hardlink moves without extra disk space or copy I/O.
* **Bootstrapping & Stacks:** [`scripts/selfhost-setup.sh`](../scripts/selfhost-setup.sh) generates `.env` (including cryptographic secrets) and launches Managerr on default port `8989`. Prebuilt container images are published at `ghcr.io/m0ritz2k19/managerr:latest`. Full 4-service Docker Compose stack (`Managerr` + `Seerr` + `SABnzbd` + `Plex`) lives in [`deploy/test-stack/`](../deploy/test-stack/README.md).

## 2. Authentication, RBAC & Security

* **Identity Providers:** Local accounts (`PBKDF2-SHA512`), Plex PIN OAuth + Plex Media Server membership verification (`Plex.tv`), Plex Home User resolution, OpenID Connect (`OIDC 1.0 + PKCE` with customizable username/email/displayName/groups claim mappings and admin/manager/requester group mappings), and WebAuthn/FIDO2 Passkeys (managed in `System -> Security` or the user profile modal, with RP settings under `Settings -> General`).
* **Roles & Scopes:** Six built-in roles (`system:owner`, `media:manager`, `media:requester`, `media:viewer`, `automation:compat`, `metrics:readonly`) plus a Custom Role Builder (`/api/v1/roles`) governing **36 `PermissionScopes`**, library allowlists, and request/exclusion quotas.
* **API Keys & Secrets:** Prefixed `mgr_live_` API keys stored as salted SHA-256 hashes (shown once at creation); upstream credentials encrypted at rest using `AES-256-GCM`.
* **Network & Abuse Hardening:** `SafeHttpClientFactory` validates outbound destinations against private/link-local SSRF targets; `/api/v1/auth/*` endpoints apply a fixed-window rate limiter (`auth_endpoints`, 60 requests/min); reverse-proxy forwarded headers are controlled via `system.security.trust_forwarded_headers` and `system.security.trusted_proxy_cidrs` (configurable via `Settings -> General` or environment variables); all administrative and auth events write to `audit_events` in `telemetry.db`.

## 3. Polymorphic 7-Vertical Catalog & UX

* **Unified Hierarchy:** Every vertical (`movies`, `tv`, `anime`, `music`, `ebooks`, `audiobooks`, `comics`) shares `Library` -> `MediaWork` -> `MediaContainer` -> `MediaUnit` -> `MediaEdition` -> `MediaFile`.
* **Dual-Format Books:** Literary works support simultaneous `ebook_slot` and `audiobook_slot` editions under a single intellectual work.
* **Catalog Filters, Sort & Back-Navigation Persistence:** Clearly separated pill groups for **Type** (media vertical), **Library** (library name), **Status** (`monitored`, `unmonitored`, `missing`, `downloaded`, `continuing`), **Classification**, and **Health** (`duplicates`, `wrong-kind`, `missing`, `complete`, `unmonitored`), plus **Date Added** sorting (`addedAt`). Active filters sync to URL search parameters + `sessionStorage` (`managerr.catalogFilterState`) and restore exact viewport scroll position (`managerr.catalogScrollY`) when navigating back from `/works/:id`.
* **Monitored Progress (`X / Y`):** Progress bars compute denominators from **monitored** episodes/units (`Y = monitored units`, not total unmonitored seasons/specials), giving accurate completion telemetry.
* **Classification, Bulk Tools & Requests:** Automated `MediaTaxonomyClassifier` (`anime-series`, `anime-movie`, `cartoon`, `live-action-series`, `live-action-movie`), multi-select bulk profile/language/relocation/tag/scan actions, token-based file renaming with live preview, `PathBrowserModal` (`GET /api/v1/filesystem`, `POST /api/v1/filesystem/directories`), `/feed/v1/calendar/managerr.ics` iCal subscription, and built-in media requests (`POST /api/v1/requests`, `POST /api/v1/requests/{id}/decide`).

## 4. Metadata Federation & External ID Crosswalk

* **20 Built-In Providers:** `tvdb`, `tmdb`, `musicbrainz`, `acoustid`, `openlibrary`, `anilist`, `anidb`, `tvmaze`, `trakt`, `discogs`, `deezer`, `indie_folder_tags`, `hardcover`, `audnexus`, `bookbrainz`, `googlebooks`, `dnb`, `mangadex`, `comicvine`, and `local_sidecar` (`.nfo`/`metadata.opf`), plus user-registered HTTP metadata webhooks (`/api/v1/metadata/webhooks`).
* **Waterfalls, Ordering & Crosswalk:** Configurable per-library and per-work provider waterfalls, locked field protection (`LockedFields`), `aired` / `dvd` / `absolute` episode ordering with TMDB collapsed anime season alignment, BFS External ID Crosswalk bridging (`external_id_links`), `LatinTitleNormalizer` (Romaji/full-width handling), and SQLite `FTS5` BM25 search.

## 5. Indexers & Multi-Wave Release Search

* **Protocol & Cardigann Support:** Native `Newznab` and `Torznab` client drivers alongside an in-process **Cardigann v12 YAML engine** reading user-supplied definitions from `/config/Definitions/Custom` or `MANAGERR_DEFINITIONS_DIR` (no third-party definitions bundled).
* **Resilience & Routing:** `t=caps` XML capability probing & caching, `FlareSolverr`, Byparr, HTTP/HTTPS, and SOCKS5 proxy support, circuit-breaker cooldowns on failing indexers, multi-wave search escalation (work + aliases + TVDB/TMDB/IMDb/Romaji), single-pass RSS sync, and `POST /api/v1/indexers/release/push` for external announce bots (`autobrr`/`irssi`).

## 6. Download Clients, CDH Import & Activity

* **12 Client Drivers:** Full queue control, category mapping, and connection testing across **`qBittorrent`**, **`Deluge`**, **`Transmission`**, **`rTorrent`**, **`Flood`**, **`Vuze`**, **`aria2`**, **`SABnzbd`**, **`NZBGet`**, **`Pneumatic`**, **`torrent_blackhole`**, and **`usenet_blackhole`**, plus **Remote Path Mappings** configurable in the UI (**Settings -> Download Clients -> Remote Path Mappings**) or via `/api/v1/downloaders/remote-paths`.
* **Localized `GrabHistory` Correlation:** Persists grabbed release titles, download IDs, and target work/unit bindings so localized releases (e.g., German or Japanese titles) match accurately back to catalog items during CDH import.
* **CDH & Atomic Imports:** Adaptive poller + instant webhook (`GET`/`POST /api/v1/downloaders/notify`) + 5-second post-grab debounce driving the Tracked Download CDH pipeline. Preserves parent season-pack/folder structures during multi-episode import, links atomically via hardlinks (`ImportMode = hardlink_or_copy`), provides Manual Import preview/execute (`/api/v1/downloaders/manual-import`), runs strike-based stalled queue sweeps (`/api/v1/downloaders/queue/sweep`), and maintains a searchable recurring release blocklist (`/api/v1/activity/blocklist`).

## 7. Composable Quality Profiles, TRaSH & Language Policies

* **Composable Design:** Reusable quality profiles support inheritance/mixins and 4-axis weighted scoring (`Quality` + `Language` + `Traits` + `Efficiency`) with dual `QualityCutoff` x `LanguageCutoff` gates, quality floors, and dynamic `original` language resolution per work.
* **TRaSH Guides & PlexUser Language Promotion:** Native importer (`POST /api/v1/profiles/import-trash`) ingests TRaSH Guides / Recyclarr Custom Format JSON definitions and flags language regex rules for uplift into dedicated Language Policies; interactive requesters can promote personal language preferences (`POST /api/v1/profiles/promote-user-language/{workId}`).

## 8. 3-Tier Language Verification, Smart Multi-Audio & Quarantine

* **3-Stage Verification:**
  1. **Stage 1 (`Container & Header Probe`):** `ffprobe` container streams, EPUB OPF, and PDF structure inspection.
  2. **Stage 2 (`Interior Text & Subtitle OCR`):** `SearchPioneer.Lingua` text identification + `Tesseract` bitmap subtitle OCR (`PGS`/`VobSub`), separating Forced vs Full subtitle tracks.
  3. **Stage 3 (`Stratified Center-Channel Speech Verification`):** `FFmpeg` 16 kHz mono center-channel extraction + `Silero VAD v5` ONNX + `Whisper.net` (`ggml-tiny-q5_1`) spoken language identification + Pearson fake-dub correlation + automatic `mkvpropedit` header patching for `und` tracks.
* **Customizable Verification Thresholds:** Operators can tune `verification.auto_pass_confidence` (default `0.80`), `verification.quarantine_min_confidence` (default `0.45`), `verification.auto_patch_header_confidence` (default `0.85`), and `verification.fake_dub_correlation_threshold` (default `0.97`) via `/api/v1/verification/settings` and the Settings UI.
* **Smart Multi-Audio Rule:** When a downloaded or scanned file already contains the desired target audio language (e.g., German) alongside additional detected audio streams (e.g., Japanese/English), Managerr corrects the catalog file's language metadata in place instead of triggering spurious redownloads or unwanted rejections.
* **Quarantine Review Workbench (`/api/v1/quarantine`):** Ambiguous files stage three 10-second Opus audio snippets (`GET /api/v1/quarantine/{id}/snippets/{index}`) for listening in the browser, with 1-click **Approve** (`/approve` with confirmed ISO code + header patch) or **Reject & Blocklist** (`/reject` + auto-search replacement).

## 9. Ecosystem Integrations, `*arr` Compat & Migration

* **`*arr` & Torznab Shims:** Virtual compatibility endpoints under `/compat/{sonarr,radarr,lidarr,readarr,prowlarr}` and `/api/v1/torznab/{indexerId}/api` let Overseerr/Jellyseerr and Prowlarr consumers connect with existing workflows intact, including Plex user tag/header correlation (`<plexId> - <username>`, `plexid-<plexId>`, `X-Plex-User-Id`) and TMDB-to-TVDB anime season expansion.
* **Notifications & Import Lists:** Dispatches grab, import, upgrade, rename, delete, health, and update events to Plex, Discord, and custom webhooks; syncs TMDb, Trakt, AniList, StevenLu, and OpenLibrary import lists with exclusion lists.
* **5-Step Migration Wizard:** Imports live APIs or offline `.db`/`.zip` backups from Sonarr, Radarr, Lidarr, Readarr, Prowlarr, NZBHydra2, and Overseerr, adopts existing library files in place preserving hardlinks, and supports **Read-Only Shadow Mode** before 1-click cutover.

## 10. UI Tour, Theme Studio & System Operations

* **Navigation & Live Updates:** Keyboard-accessible `Cmd+K`/`Ctrl+K` Command Palette (`CommandPaletteModal`), SignalR hub (`/api/v1/realtime`) with automatic SSE fallback (`/api/v1/realtime/stream`), and Wanted views (`/wanted/missing`, `/wanted/cutoff`) supporting per-unit and bulk searches (`POST /api/v1/wanted/search` with modes `selected`, `all_missing`, `all_cutoff_unmet`).
* **Theme Studio:** 5 built-in presets (`seafoam_jade`, `royal_amethyst`, `cyber_chartreuse`, `brushed_copper`, `nordic_periwinkle`) + `custom` across `system`/`dark`/`oled`/`light` base modes, 19 CSS custom properties, automatic WCAG AA foreground contrast calculation (`computeContrastingForeground` at luminance threshold `0.42` for >= 4.5:1 ratio), and `managerr-theme/v1` JSON import/export.
* **Health Self-Healing, 10 Customizable Tasks, Retention & Hot Backups:** Automated remediation via `POST /api/v1/system/health/resolve/{actionKey}` (`CanonicalizeQualities`, `PruneMissingFiles`, `DeduplicateUnitFiles`, `ReclassifyMismatchedWorks`), **10 scheduled background tasks** (`RefreshMonitoredDownloads`, `RssSync`, `QueueHealthCheck`, `HealthCheck`, `DiskScanLibraries`, `ImportListSync`, `RefreshCatalogMetadata`, `SqliteBackup`, `RetentionAndVacuum`, `CleanerrEvaluation`) with customizable intervals and enable/disable toggles, configurable retention and backup limits (`system.retention_policy`, `system.backups.max_retained`), and non-blocking SQLite WAL snapshots via `VACUUM INTO`.

## 11. Agent-Ready REST API v1 & Filtering

* OpenAPI 3.1 JSON contracts, RFC 9457 Problem Details error payloads, and `Idempotency-Key` protection on state-mutating endpoints.
* Full server-side `q` search, filter, sort (`sort` / `order`), and pagination (`limit` / `offset` / `page` / `pageSize`) across `GET /api/v1/catalog/works`, `POST /api/v1/indexers/search`, `GET /api/v1/catalog/works/{id}/releases`, `GET /api/v1/wanted/{missing,cutoff-unmet}`, `GET /api/v1/activity/history`, and `GET /api/v1/activity/blocklist`.

## 12. Built-In Streamable HTTP MCP Server

* Served directly at `POST /api/v1/mcp` and `POST /mcp` (`2025-06-18`, `2025-03-26`, `2024-11-05`) with JSON-RPC 2.0 single & batch execution, togglable via `system.mcp.enabled` (General Settings / System Security).
* Dynamically filters `tools/list` and authorizes every `tools/call` against the caller's RBAC `PermissionScopes` (returning `ERR_RBAC_FORBIDDEN` when missing scopes), exposing **40 tools** across Catalog Management (12), Operations & Acquisition (18), and Cleanerr Lifecycle (10). See [`docs/09-MCP-SERVER-AND-AGENT-API.md`](./09-MCP-SERVER-AND-AGENT-API.md) for the exact schema of every tool.

## 13. Cleanerr Lifecycle Studio & Interactive Discord Bot

* **Visual DAG Studio & Maintainerr-Compatible Rule Builder:** Build lifecycle rules either visually via the `@xyflow/react` node-graph Workflow Studio or with structured `AND`/`OR` rule groups (`RuleGroup`), complete with YAML import/export, node-level dry-run simulation, and per-item condition tracing.
* **Whole-Series Handling & Native Leaving Soon Queue:** Evaluates TV series at the whole-series level, stages matching items in Managerr's native **Leaving Soon** queue without altering Plex collections, supports grace-period postponement/rescue, tracks exclusions with whole-series family escalation, and checks filesystem hardlinks before deletion.
* **Customizable Settings & Discord Bot:** Configurable default grace days (`cleanerr.default_grace_days`), final-warning lead hours (`cleanerr.discord_final_warning_hours`), Seerr sync settings (`cleanerr.seerr_url`, `cleanerr.seerr_api_key`), and an interactive Discord Bot (Gateway WebSocket + webhook notifications) supporting `/cleanerr` slash commands, interactive buttons (Rescue, Exclude, Keep), and HMAC-signed magic links.

---

## Complete Documentation Index

### Repository Specifications ([`docs/`](./))
* [00: Vision, Scope & Roadmap](./00-VISION-AND-ROADMAP.md)
* [01: Unified Architecture, Embedded SQLite & Code Reuse](./01-UNIFIED-ARCHITECTURE-AND-CODE-REUSE.md)
* [02: Identity, RBAC & API Spec](./02-SECURITY-AND-API-SPEC.md)
* [03: Embedded Indexer Engine & Download Client Orchestration](./03-INDEXERS-AND-DOWNLOADERS.md)
* [04: Composable Profiles, Multi-Axis Scoring & Language Upgrades](./04-PROFILES-SCORING-AND-LANGUAGE-UPGRADES.md)
* [05: Multi-Tier File Language Verification & Quarantine Pipeline](./05-LANGUAGE-VERIFICATION-PIPELINE.md)
* [06: Pluggable Metadata Provider Federation & ID Crosswalk Graph](./06-METADATA-PROVIDER-ARCHITECTURE.md)
* [08: `*arr` & Overseerr Migration + Shadow-Sync Engine](./08-MIGRATION-ENGINE.md)
* [09: MCP Server & AI Agent Guide](./09-MCP-SERVER-AND-AGENT-API.md)
* [10: Complete Feature Reference (`v0.2.0`)](./10-COMPLETE-FEATURE-REFERENCE.md)
* [Self-Hosting Deployment Guide](./SELF-HOSTING.md)

### GitHub Wiki Pages
* [Wiki Home](https://github.com/m0ritz2k19/managerr/wiki)
* [01: Getting Started & Self-Hosting](https://github.com/m0ritz2k19/managerr/wiki/01-Getting-Started-and-Self-Hosting)
* [02: Authentication, RBAC & Security](https://github.com/m0ritz2k19/managerr/wiki/02-Authentication-RBAC-and-Security)
* [03: Catalog, Libraries & Media Management](https://github.com/m0ritz2k19/managerr/wiki/03-Catalog-Libraries-and-Media-Management)
* [04: Metadata Federation & Crosswalk](https://github.com/m0ritz2k19/managerr/wiki/04-Metadata-Federation-and-Crosswalk)
* [05: Indexers & Release Search](https://github.com/m0ritz2k19/managerr/wiki/05-Indexers-and-Release-Search)
* [06: Download Clients, Import & Activity](https://github.com/m0ritz2k19/managerr/wiki/06-Download-Clients-Import-and-Activity)
* [07: Quality Profiles, Scoring & Language Policies](https://github.com/m0ritz2k19/managerr/wiki/07-Quality-Profiles-Scoring-and-Language-Policies)
* [08: Language Verification & Quarantine](https://github.com/m0ritz2k19/managerr/wiki/08-Language-Verification-and-Quarantine)
* [09: Ecosystem, Arr Compat & Migration](https://github.com/m0ritz2k19/managerr/wiki/09-Ecosystem-Arr-Compat-and-Migration)
* [10: UI Tour, Theme Studio & System Operations](https://github.com/m0ritz2k19/managerr/wiki/10-UI-Tour-Theme-Studio-and-System-Operations)
* [11: REST API v1 Reference](https://github.com/m0ritz2k19/managerr/wiki/11-REST-API-v1-Reference)
* [12: MCP Server & AI Agent Guide](https://github.com/m0ritz2k19/managerr/wiki/12-MCP-Server-and-AI-Agent-Guide)
* [13: Cleanerr Lifecycle Studio and Discord Bot](https://github.com/m0ritz2k19/managerr/wiki/13-Cleanerr-Lifecycle-Studio-and-Discord-Bot)
