# Managerr

**Managerr** (`v0.2.0`) is a self-hosted media lifecycle platform that replaces **Sonarr**, **Radarr**, **Lidarr**, **Readarr**, **Prowlarr / NZBHydra2**, and **Maintainerr / Cleanerr** in a single `.NET 10` and `React 19 / TypeScript` service backed by embedded SQLite (`WAL` + `FTS5`).

> **Documentation:** Browse the in-repo [`wiki/Home.md`](./wiki/Home.md) or the live **[GitHub Wiki](https://github.com/m0ritz2k19/managerr/wiki)**, or see the complete feature inventory in [`docs/10-COMPLETE-FEATURE-REFERENCE.md`](./docs/10-COMPLETE-FEATURE-REFERENCE.md).

---

## Quick Start

```bash
git clone https://github.com/m0ritz2k19/managerr.git && cd managerr
scripts/selfhost-setup.sh --data-dir /srv/data
```

The setup script writes `.env`, creates `/config` and `/data` directories, and starts Managerr on port `8989`.

* **Use the prebuilt GHCR image (default):** `docker compose up -d` pulls the multi-arch `ghcr.io/m0ritz2k19/managerr:latest` image (all state is stored in embedded SQLite `WAL` + `FTS5` databases in `/config`, with no external database containers).
* **Build locally from source:** `docker compose -f compose.yaml -f compose.build.yaml up -d --build` (or pass `--build` to `scripts/selfhost-setup.sh`).
* **Self-hosting guide:** See [`docs/SELF-HOSTING.md`](./docs/SELF-HOSTING.md) for directory layout, authentication setup, reverse proxy configuration, remote path mappings, Cleanerr/Discord setup, updates, and backups.
* **4-service test stack (`Managerr` + `Seerr` + `SABnzbd` + `Plex`):** See [`deploy/test-stack/README.md`](./deploy/test-stack/README.md).

---

## Core Capabilities

* **Polymorphic 7-Vertical Catalog:**
  * Manages **Movies**, **TV Series**, **Anime**, **Music**, **Ebooks**, **Audiobooks**, and **Comics/Manga** under a shared hierarchy (`MediaWork` -> `MediaContainer` -> `MediaUnit` -> `MediaEdition` -> `MediaFile`), supporting dual Ebook and Audiobook slots on a single book record.
  * Separated catalog filters (**Type**, **Library**, **Status**, **Classification**, **Health**) with URL and session persistence, **Date Added** sorting, and monitored `X / Y` progress bars.
  * 20 built-in metadata providers (`TheTVDB`, `TMDB`, `MusicBrainz`, `AcoustID`, `Open Library`, `AniList`, `AniDB`, `TVMaze`, `Trakt`, `Discogs`, `Deezer`, `Hardcover`, `Audnexus`, `BookBrainz`, `Google Books`, `DNB`, `MangaDex`, `ComicVine`, `local_sidecar`, `indie_folder_tags`), custom HTTP webhooks, per-library waterfalls, and external ID crosswalks.

* **Embedded Indexer & 12 Download Clients:**
  * **Indexers:** Native `Newznab` and `Torznab` support alongside an in-process **Cardigann v12 YAML engine** reading user-supplied definitions from `/config/Definitions/Custom` or `MANAGERR_DEFINITIONS_DIR` (no third-party tracker definitions are bundled). Supports `FlareSolverr`, Byparr, HTTP/SOCKS5 proxies, and single-pass RSS polling.
  * **12 Download Clients:** Queue tracking, category routing, pause/resume/priority control, removal (`deleteData`), and import orchestration across **`qBittorrent`**, **`Deluge`**, **`Transmission`**, **`rTorrent`**, **`Flood`**, **`Vuze`**, **`aria2`**, **`SABnzbd`**, **`NZBGet`**, **`Pneumatic`**, **`torrent_blackhole`**, and **`usenet_blackhole`**.
  * Includes a **Remote Path Mappings UI** (**Settings -> Download Clients -> Remote Path Mappings**) and localized **`GrabHistory` correlation** so localized release titles map reliably back to the grabbed work and units on import.

* **Composable Profiles, Language Policies & 3-Tier Language Verification:**
  * Composable **Quality Profiles**, **Trait Mixins**, and **Language Policies** with 4-axis scoring (`Quality` + `Language` + `Traits` + `Efficiency`), dual `QualityCutoff` x `LanguageCutoff` gates, dynamic `original` language resolution, and a native **TRaSH Guides / Recyclarr importer**.
  * **3-Tier Post-Download Language Verification Pipeline** with configurable confidence thresholds (`auto_pass`, `quarantine_min`, `auto_patch_header`, `fake_dub_correlation`):
    1. **Stage 1:** `ffprobe`, `EPUB` OPF, and `PDF` container/header inspection.
    2. **Stage 2:** `SearchPioneer.Lingua` text language identification and `Tesseract` OCR on bitmap subtitles (`PGS`/`VobSub`), separating Forced vs Full subtitle tracks.
    3. **Stage 3:** `FFmpeg` 16 kHz mono center-channel extraction, `Silero VAD v5` ONNX voice activity detection, `Whisper.net` spoken language identification, Pearson waveform correlation for fake duplicate-dub detection, automatic `mkvpropedit` header patching for `und` tracks, and a browser **Quarantine Review** queue with playable Opus snippets.

* **Cleanerr Lifecycle DAG Studio, Rule Builder & Interactive Discord Bot:**
  * Visual `@xyflow/react` **Workflow Studio** alongside a **Maintainerr-compatible Rule Builder** (`AND`/`OR` rule groups, YAML import/export, and dry-run simulation).
  * Handles TV series as whole series, manages a native **Leaving Soon** deletion queue without modifying Plex collections, and enforces **hardlink protection** before deleting files.
  * Interactive **Discord Bot** (Gateway WebSocket + webhook notifications) featuring `/cleanerr` slash commands, interactive Rescue/Exclude/Keep buttons, and HMAC-signed magic links.

* **Multi-Provider Auth & RBAC:**
  * Supports **Local Accounts** (`PBKDF2-SHA512`), **Plex PIN OAuth** (with PMS server-membership gate and Plex Home user switching), **OIDC 1.0 + PKCE** (with customizable claim mappings and role group mappings), and **WebAuthn Passkeys**.
  * **6 built-in roles** (`system:owner`, `media:manager`, `media:requester`, `media:viewer`, `automation:compat`, `metrics:readonly`) plus a **Custom Role Builder** across **36 permission scopes**, library allowlists, and request/exclusion quotas.
  * `AES-256-GCM` secret encryption at rest, `SafeHttpClientFactory` SSRF egress guard, `mgr_live_` scoped API keys, and configurable reverse-proxy forwarded-header trust (`TrustForwardedHeaders` and `TrustedProxyCidrs`).

* **Ecosystem Compatibility, Migration & Operations:**
  * Virtual `/compat/{sonarr,radarr,lidarr,readarr,prowlarr}` shims (with Seerr Plex user correlation and TMDB-to-TVDB anime season expansion) plus a 5-step **Migration Wizard** for live APIs or offline `.db`/`.zip` backups (`Sonarr`, `Radarr`, `Lidarr`, `Readarr`, `Prowlarr`, `NZBHydra2`, `Overseerr`) with hardlink-preserving adoption and Shadow Mode.
  * **10 customizable scheduled tasks** (including `CleanerrEvaluation`), non-blocking SQLite `VACUUM INTO` hot backups, **Theme Studio** (5 presets, 19 CSS tokens, WCAG AA contrast check, JSON import/export), OpenAPI 3.1 **REST API v1**, and a **40-tool Streamable HTTP MCP server** at `/api/v1/mcp` and `/mcp` (togglable via `system.mcp.enabled`).

---

## Documentation & Community

* **Wiki (in-repo):** [`wiki/Home.md`](./wiki/Home.md)
* **Live GitHub Wiki:** [https://github.com/m0ritz2k19/managerr/wiki](https://github.com/m0ritz2k19/managerr/wiki)
* **Self-Hosting Guide:** [`docs/SELF-HOSTING.md`](./docs/SELF-HOSTING.md)
* **4-Service Test Stack:** [`deploy/test-stack/README.md`](./deploy/test-stack/README.md)
* **Contributing:** [`CONTRIBUTING.md`](./CONTRIBUTING.md)
* **Security Policy:** [`SECURITY.md`](./SECURITY.md)

### Architecture & Specification Suite ([`docs/`](./docs/))

1. [**00: Vision, Scope & Roadmap**](./docs/00-VISION-AND-ROADMAP.md)
2. [**01: Unified Architecture, Embedded SQLite (`WAL`+`FTS5`), Polymorphic Data Model & Upstream Code Reuse**](./docs/01-UNIFIED-ARCHITECTURE-AND-CODE-REUSE.md)
3. [**02: Identity, Preset + Custom RBAC Roles, Native API v1 & Arr Compatibility Shims**](./docs/02-SECURITY-AND-API-SPEC.md)
4. [**03: Embedded Indexer Engine & Download Client Orchestration**](./docs/03-INDEXERS-AND-DOWNLOADERS.md)
5. [**04: Composable Profiles, Multi-Axis Scoring & Language Upgrade Engine**](./docs/04-PROFILES-SCORING-AND-LANGUAGE-UPGRADES.md)
6. [**05: Multi-Tier File Language Verification & Quarantine Pipeline**](./docs/05-LANGUAGE-VERIFICATION-PIPELINE.md)
7. [**06: Pluggable Metadata Provider Federation & ID Crosswalk Graph**](./docs/06-METADATA-PROVIDER-ARCHITECTURE.md)
8. [**08: Arr & Overseerr Migration + Shadow-Sync Engine**](./docs/08-MIGRATION-ENGINE.md)
9. [**09: MCP Server & AI Agent API Guide**](./docs/09-MCP-SERVER-AND-AGENT-API.md)
10. [**10: Complete Feature Reference (`v0.2.0`) & Documentation Index**](./docs/10-COMPLETE-FEATURE-REFERENCE.md)

---

## Upstream Reference Repositories ([`upstream/`](./upstream/))

Upstream repositories are tracked as shallow Git submodules under [`upstream/`](./upstream/) as reference sources for C# subsystems, React views, database migrations, and Cardigann patterns:

| Submodule Path | Upstream Repository | Primary Reuse Role in Managerr |
| :--- | :--- | :--- |
| [`upstream/Sonarr`](./upstream/Sonarr) | `Sonarr/Sonarr` (`v5` / `.NET 10`) | 12 Download clients, Video `ffprobe` MediaInfo, TV/Anime parser, Vite 8 + TanStack Query 5 UI patterns |
| [`upstream/Radarr`](./upstream/Radarr) | `Radarr/Radarr` (`.NET 10`) | Movie editions, multi-stream MediaInfo migrations, CustomFormat specifications |
| [`upstream/Lidarr`](./upstream/Lidarr) | `Lidarr/Lidarr` (`.NET 8`) | `TagLibSharp` `AudioTagService`, Chromaprint/AcoustID integration, MusicBrainz release/track modeling |
| [`upstream/Readarr`](./upstream/Readarr) | `Readarr/Readarr` (`.NET 6`) | `EpubTag` (`OPF`), `AzwTag` (`MOBI`/`AZW3`), `AudioTag` (`M4B`), Author/Book/Edition domain reference |
| [`upstream/Prowlarr`](./upstream/Prowlarr) | `Prowlarr/Prowlarr` (`.NET 10`) | In-process Cardigann v12 YAML engine, Torznab/Newznab, FlareSolverr/SOCKS5 proxies, rate-limiters |

To initialize or refresh all upstream submodules on a fresh clone:
```bash
./scripts/sync-upstream.sh
```

---

## License

Managerr is licensed under the **GNU General Public License v3.0 or later** (see [`LICENSE`](./LICENSE)).
