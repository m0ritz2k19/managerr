# Getting Started & Self-Hosting Guide

Deployment, configuration, operations, updates, and backups for **Managerr**.

---

## 1. Hardware & Software Requirements

Managerr runs as a compiled .NET 10 application with embedded SQLite databases, using fewer resources than running 5 to 7 separate media managers alongside external PostgreSQL or Redis containers.

### Minimum System Specifications
- **Operating System:** Linux (Kernel 5.4+; Ubuntu 22.04+, Debian 12+, Arch Linux, Alpine, or Fedora) with Docker Engine 24.0+ and the Docker Compose v2 plugin (`docker compose version`).
- **CPU Architecture:** `x86_64` (amd64) or `arm64` (aarch64).
- **CPU Cores:** 2 physical cores minimum. 4 or more cores recommended if Stage 3 Whisper speech language identification is enabled.
- **RAM:**
  - *Standard Operation:* 1 GB to 2 GB RAM.
  - *With Stage 3 Speech Verification (`Whisper.net` tiny model):* 3 GB to 4 GB RAM.
- **Disk Storage:**
  - *Root/Config Disk:* 10 GB SSD space for `/config` (stores SQLite databases, search indexes, and temporary speech snippets).
  - *Media Disk:* High-capacity HDD/SSD storage mounted on a single filesystem for downloads and library media.

### Network Prerequisites
- Default HTTP port: `8989` (configurable via `MANAGERR_PORT`).
- Egress network access to your Usenet providers, torrent trackers, metadata APIs (TMDB, TVDB, MusicBrainz), and `plex.tv` (unless operating in offline break-glass mode).

---

## 2. Quick-Start Installation via `selfhost-setup.sh`

The fastest way to install Managerr on a Linux host is using the interactive setup script:

```bash
git clone https://github.com/m0ritz2k19/managerr.git
cd managerr
scripts/selfhost-setup.sh --data-dir /srv/data
```

### What `scripts/selfhost-setup.sh` Automates:
1. **Docker & Compose Verification:** Checks that Docker is installed, running, and accessible without root, and confirms the `compose` plugin is present.
2. **Directory & Permission Setup:** Creates the root data directory (`/srv/data`) and standard subdirectories (`downloads/` and `media/{movies,tv,anime,music,books,comics}`).
3. **PUID/PGID Detection:** Automatically detects the user ID and group ID of the owner of `DATA_DIR` (`stat -c %u` and `stat -c %g`) and writes them into `.env` so container file operations avoid permission conflicts.
4. **Time Zone Alignment:** Auto-detects the host system time zone (`timedatectl show -p Timezone`) and sets `TZ` accordingly.
5. **Config & Image Initialization:** Creates the `./appdata/config` folder, writes `.env`, pulls or builds the container image, and waits for `http://127.0.0.1:8989/api/v1/system/health` to report `{"status":"healthy"}`.

### CLI Flags Reference:
```bash
scripts/selfhost-setup.sh [OPTIONS]

Options:
  --data-dir PATH    Path to the shared data folder on the host (e.g. /srv/data).
  --port PORT        Host port to bind Managerr (default: 8989).
  --no-start         Write .env and create directories without building or starting the container.
  -h, --help         Show help message.
```

---

## 3. Atomic Hardlink-Friendly Storage Layout (`/data`)

Managerr uses Unix hardlinks (`link(2)`) and atomic renames (`rename(2)`) to import completed downloads into media libraries without duplicating disk space.

### The Single-Mount Rule
For hardlinks to work, **downloads and media libraries must live on the same physical filesystem and be passed into Docker as a single volume mount (`/data`)**.

If you mount `/downloads` and `/movies` as two separate Docker volumes, Docker treats them as separate filesystems. When Managerr imports a completed download, the operating system performs a slow copy across mounts instead of an atomic hardlink.

### Recommended Directory Structure

```text
/srv/data                         <--- Mounted into containers as /data
├── downloads                     <--- Download clients save here
│   ├── complete
│   │   ├── movies
│   │   ├── tv
│   │   ├── anime
│   │   ├── music
│   │   └── books
│   └── incomplete
└── media                         <--- Managerr organizes files here
    ├── movies
    │   └── Dune Part Two (2024) [imdbid-tt15239678]
    │       └── Dune Part Two (2024) [WEBDL-2160p][HDR10][EAC3 5.1][DE+EN].mkv
    ├── tv
    │   └── Severance (2022) [tvdbid-371980]
    │       └── Season 01
    │           └── Severance - S01E01 - Good News About Hell [WEBDL-1080p].mkv
    ├── anime
    ├── music
    ├── books
    └── comics
```

When configured this way:
1. Usenet and torrent clients download files into `/data/downloads/incomplete`.
2. Upon completion, clients move files to `/data/downloads/complete/<category>`.
3. Managerr creates an atomic hardlink from `/data/downloads/complete/...` into `/data/media/...`.
4. The download client continues seeding torrents from `/data/downloads`, while media servers stream from `/data/media`, consuming no duplicate disk space.
5. **Cleanerr Seeding Protection:** Cleanerr scans `cleanerr.torrent_downloads_path` (default `/data/downloads`) to correlate inodes against active seeding hardlinks before deleting media files. Keeping `/data` as a single mount ensures inode comparison works reliably.

---

## 4. Docker Compose Deployment (`compose.yaml`)

For manual deployment via Docker Compose, use `compose.yaml` in the root repository. You can either build locally from source or pull the prebuilt multi-arch image `ghcr.io/m0ritz2k19/managerr:latest`.

### `compose.yaml`
```yaml
services:
  managerr:
    build:
      context: .
      dockerfile: Dockerfile
    image: ${MANAGERR_IMAGE:-ghcr.io/m0ritz2k19/managerr:latest}
    container_name: ${CONTAINER_NAME:-managerr}
    restart: unless-stopped
    # Runs as this user, so files in CONFIG_DIR and DATA_DIR belong to you, not root
    user: "${PUID:-1000}:${PGID:-1000}"
    ports:
      - "${MANAGERR_BIND_IP:-127.0.0.1}:${MANAGERR_PORT:-8989}:8989"
    environment:
      TZ: ${TZ:-Europe/Berlin}
      ASPNETCORE_URLS: "http://+:8989"
      Managerr__ConfigDirectory: /config
      SafeHttpEgress__AllowedPrivateCidrs__0: "192.168.0.0/16"
      SafeHttpEgress__AllowedPrivateCidrs__1: "172.16.0.0/12"
      SafeHttpEgress__AllowedPrivateCidrs__2: "10.0.0.0/8"
      # Optional: allow Tailscale CGNAT subnet for Plex / download clients
      # SafeHttpEgress__AllowedPrivateCidrs__3: "100.64.0.0/10"
      # Additional configuration keys can be injected here or via .env
    volumes:
      # Embedded SQLite databases, master encryption key, and telemetry
      - ${CONFIG_DIR:-./appdata/config}:/config
      # Unified download and library storage for atomic hardlinks
      - ${DATA_DIR:-./data}:/data
    extra_hosts:
      - "host.docker.internal:host-gateway"
    security_opt:
      - no-new-privileges:true
    cap_drop:
      - ALL
    healthcheck:
      test: ["CMD", "curl", "-fsS", "http://127.0.0.1:8989/api/v1/system/health"]
      interval: 30s
      timeout: 5s
      retries: 3
      start_period: 15s
    logging:
      driver: json-file
      options:
        max-size: "10m"
        max-file: "3"
```

### Initial `.env` Configuration
Copy `.env.example` to `.env` and set paths, image tag, and user IDs:

```bash
cp .env.example .env
```

```ini
# Prebuilt container image (or set managerr:local when building from source)
MANAGERR_IMAGE=ghcr.io/m0ritz2k19/managerr:latest

# Required: host root folder containing downloads and media
DATA_DIR=/srv/data

# Location for configuration databases and master key
CONFIG_DIR=./appdata/config

# Host user and group ID owning DATA_DIR (run `id -u` and `id -g`)
PUID=1000
PGID=1000

# Bind IP: use 127.0.0.1 for reverse proxy setups or 0.0.0.0 for LAN exposure
MANAGERR_BIND_IP=127.0.0.1
MANAGERR_PORT=8989

# Time zone
TZ=Europe/Berlin
CONTAINER_NAME=managerr
```

Launch the container:
```bash
# Using prebuilt GHCR image:
docker compose pull && docker compose up -d

# Or building locally from source:
docker compose up -d --build
docker compose logs -f managerr
```

---

## 5. Full 4-Service Test Stack (`deploy/test-stack`)

For evaluation, automated testing, or homelab prototyping, Managerr provides a 4-service Docker Compose stack located in `deploy/test-stack`:

| Service | Container Image | Port | Container URL | Purpose |
| :--- | :--- | :--- | :--- | :--- |
| **Managerr** | `managerr:local` (built from source) | `8989` | `http://managerr:8989` | Unified catalog, indexers, download client orchestrator, Cleanerr, and verification |
| **Seerr** | `ghcr.io/seerr-team/seerr:v3.5.2` | `5055` | `http://seerr:5055` | Media discovery and user request portal |
| **SABnzbd** | `lscr.io/linuxserver/sabnzbd:latest` | `8080` | `http://sabnzbd:8080` | Usenet download client |
| **Plex** | `lscr.io/linuxserver/plex:latest` | `32400` | `http://plex:32400` | Streaming media server |

All four services connect to a shared Docker bridge network (`managerr-stack`) and mount `${DATA_DIR}` at `/data`, ensuring hardlink imports and matching path mappings.

### Quick Start with the Test Stack:
```bash
cd deploy/test-stack
cp .env.example .env

# Edit .env and configure MANAGERR_ADMIN_PASS, PUID, PGID, and PLEX_CLAIM (optional)
mkdir -p appdata/{managerr,sabnzbd,seerr,plex} \
         data/downloads/{incomplete,complete} \
         data/media/{movies,tv,anime,music,books}

docker compose up -d --build
```

### Wiring the Stack Components:
1. **SABnzbd:** Set temporary download directory to `/data/downloads/incomplete` and completed directory to `/data/downloads/complete`. Copy the API key from **Settings -> General**.
2. **Plex:** Create movie library at `/data/media/movies` and TV library at `/data/media/tv`.
3. **Managerr:** Add download client (**Settings -> Download Clients -> Add -> SABnzbd** with host `sabnzbd`, port `8080`, and your SABnzbd API key). Add media libraries pointing to `/data/media/...`.
4. **Seerr:** Add a Radarr server with host `managerr`, port `8989`, URL base `/compat/radarr`, and the Managerr API key. Add a Sonarr server with host `managerr`, port `8989`, URL base `/compat/sonarr`.

---

## 6. Embedded 3-File SQLite Storage Architecture

Managerr operates on embedded SQLite with Write-Ahead Logging (`WAL`) and Full-Text Search (`FTS5`), maintaining three database files inside `/config`:

```text
/config/
├── managerr.db          # Core transactional state
├── managerr.db-wal
├── managerr.db-shm
├── cache.db             # Ephemeral metadata and rate-limit caches
├── cache.db-wal
├── telemetry.db         # High-throughput logs, verification outputs, and audit events
├── telemetry.db-wal
└── master.key           # HKDF-derived master encryption key (critical)
```

### Database Responsibilities:
1. **`managerr.db` (Core State):**
   - Media catalog (`MediaWork`, `MediaContainer`, `MediaUnit`, `MediaEdition`, `MediaFile`).
   - User identities (`Users`, `UserRoles`, `WebAuthnCredentials`, `UserSessions`).
   - Custom RBAC roles, quality profiles, language policies.
   - Cleanerr workflows, Leaving Soon deletion queue, and exclusion rules.
   - Configured indexers, download clients, and encrypted secrets.
   - Sized compactly (~20 MB to 100 MB for typical instances).
2. **`cache.db` (Ephemeral & High-Churn State):**
   - External metadata provider JSON responses (TVDB, TMDB, MusicBrainz).
   - Cross-walk ID translation graph.
   - Indexer rate-limiting tokens and query windows.
   - Safe to delete or clear at any time; will auto-rebuild.
3. **`telemetry.db` (Audit & High-Throughput Logs):**
   - Append-only security `AuditEvents`.
   - Stage 1, Stage 2, and Stage 3 language verification job execution logs.
   - Background task history and rolling retention logs.

### High-Concurrency Connection Pragmas
To prevent `SQLITE_BUSY` errors under heavy parallel RSS polling and background verification jobs, Managerr executes the following pragmas on every opened database connection:

```sql
PRAGMA journal_mode = WAL;
PRAGMA synchronous = NORMAL;
PRAGMA busy_timeout = 10000;
PRAGMA temp_store = MEMORY;
PRAGMA mmap_size = 268435456;
PRAGMA foreign_keys = ON;
```

In addition, `SqliteDbFactory` enforces an in-process serialized write coordinator (`SemaphoreSlim(1, 1)`) per database file, ensuring that multi-threaded writes queue cleanly while parallel reads execute concurrently.

---

## 7. Environment Variables & `*_FILE` Secret Injection

Managerr uses ASP.NET Core hierarchical configuration. Nested properties use double underscores (`__`) in environment variables or colons (`:`) in JSON configuration.

### Core Configuration Reference

| Environment Variable | Default Value | Description |
| :--- | :--- | :--- |
| `MANAGERR_IMAGE` | `ghcr.io/m0ritz2k19/managerr:latest` | Docker image reference used by `compose.yaml`. |
| `DATA_DIR` | *(Required)* | Host directory mounted to `/data` containing downloads and media. |
| `CONFIG_DIR` | `./appdata/config` | Host directory mounted to `/config` storing SQLite DBs and master key. |
| `PUID` / `PGID` | `1000` / `1000` | UID and GID for file permissions inside the container. |
| `MANAGERR_BIND_IP` | `127.0.0.1` | Host interface to bind. Use `0.0.0.0` to expose directly to the LAN. |
| `MANAGERR_PORT` | `8989` | Host listening port for web UI and REST API. |
| `TZ` | `Europe/Berlin` | System time zone for scheduled tasks, RSS parsing, and logs. |
| `ASPNETCORE_URLS` | `http://+:8989` | Internal Kestrel HTTP binding inside the container. |
| `Managerr__ConfigDirectory` | `/config` | Internal container path where SQLite databases are located. |
| `Managerr__Mcp__Enabled` | `true` | Enables or disables the `/api/v1/mcp` and `/mcp` endpoints at startup. |
| `Managerr__Auth__TrustForwardedHeaders` | `false` | Enables `X-Forwarded-For`, `X-Forwarded-Proto`, and `X-Forwarded-Host` processing when behind a trusted reverse proxy (requires non-empty `TrustedProxyCidrs`). |
| `Managerr__Auth__TrustedProxyCidrs__0` | *(Empty)* | Trusted reverse-proxy IP or CIDR ranges (required non-empty when `TrustForwardedHeaders=true`, e.g., `172.16.0.0/12`, `100.64.0.0/10` for Tailscale). |
| `Managerr__Auth__AllowedHosts__0` | *(Empty)* | Allowed hostnames or wildcard domains (e.g., `managerr.example.com`, `*.example.com`) accepted when applying `X-Forwarded-Host`. |
| `SafeHttpEgress__AllowedPrivateCidrs__0` | `192.168.0.0/16` | Allowlist CIDR for homelab HTTP egress (SSRF guard). |
| `SafeHttpEgress__AllowedPrivateCidrs__1` | `172.16.0.0/12` | Allowlist CIDR for Docker bridge networks. |
| `SafeHttpEgress__AllowedPrivateCidrs__2` | `10.0.0.0/8` | Allowlist CIDR for homelab private subnets. |
| `SafeHttpEgress__AllowedPrivateCidrs__3` | *(Optional)* | Additional private/CGNAT CIDR, such as `100.64.0.0/10` for Tailscale. |
| `SafeHttpEgress__AllowedInternalHosts__*` | `[]` | Explicit container hostnames allowed for SSRF egress (e.g. `sabnzbd`, `plex`). |

### Docker Secrets & `*_FILE` Convention (`FileSecretConfigurationProvider`)
Configuration keys ending with `_FILE` tell Managerr to read their value from the referenced file path (e.g. `/run/secrets/...`):

- **Supported Prefixes:** `Managerr__`, `MANAGERR_`, and `SafeHttpEgress__`.
- **File Size Safety Limit:** Files larger than 64 KB are rejected to prevent memory exhaustion.
- **Example Usage:**
  ```yaml
  environment:
    Managerr__MasterKey_FILE: /run/secrets/managerr_master_key
    Managerr__Auth__Oidc__ClientSecret_FILE: /run/secrets/oidc_client_secret
    Managerr__Auth__Local__BootstrapAdmin__Password_FILE: /run/secrets/admin_password
  secrets:
    - managerr_master_key
    - oidc_client_secret
    - admin_password
  ```

---

## 8. Headless Admin & Testbed Bootstrap Options

For automated deployments (Ansible, Terraform, GitOps) or headless Docker provisioning, Managerr provides two bootstrapping mechanisms:

### Option A: `BootstrapAdmin` (Production Headless Owner Setup)
Provisions an initial administrative user on container startup. If no owner exists yet, this user claims instance ownership and receives the immutable `Admin` role:

```yaml
environment:
  Managerr__Auth__Local__BootstrapAdmin__Username: "admin"
  Managerr__Auth__Local__BootstrapAdmin__Email: "admin@homelab.local"
  Managerr__Auth__Local__BootstrapAdmin__DisplayName: "Homelab Administrator"
  Managerr__Auth__Local__BootstrapAdmin__Password: "<SECURE_BOOTSTRAP_PASSWORD>"
  # Or inject from a secret file:
  # Managerr__Auth__Local__BootstrapAdmin__Password_FILE: /run/secrets/admin_password
```

If the instance already has an owner, this mechanism creates a local user with administrative privileges or updates the break-glass password if the account already exists.

### Option B: `TestbedBootstrap` (CI/CD and Isolated Testing)
Designed for integration test environments without connection to `plex.tv`. Automatically provisions a mock Plex Home User with Plex ID `999000001L`, assigns the `Admin` role, and enables immediate break-glass authentication:

```yaml
environment:
  Managerr__TestbedBootstrap__Enabled: "true"
  Managerr__TestbedBootstrap__Username: "testadmin"
  Managerr__TestbedBootstrap__Password: "<TEST_PASSWORD>"
  Managerr__TestbedBootstrap__PlexServerUrl: "http://plex:32400"
```

---

## 9. Updates & Database Migrations

Updating Managerr with prebuilt images:

```bash
docker compose pull
docker compose up -d
```

Or when building locally from git source:

```bash
git pull
docker compose up -d --build
```

### Automatic Schema Migrations
On startup, Managerr's `SchemaMigrator` scans embedded assembly resources for numbered SQL migration scripts and executes pending migrations inside an atomic transaction on `managerr.db`. Schema versions are recorded in the `schema_migrations` table. Migrations are non-destructive and backward-compatible.

---

## 10. SQLite Backup & Disaster Recovery

### What Needs to Be Backed Up
Back up the entire `/config` directory. The critical assets are:
1. **`managerr.db`**: Contains catalog, users, indexers, downloaders, Cleanerr workflows, and settings.
2. **`master.key`**: Required. Contains the encryption master key. Download client passwords, indexer API keys, Discord bot tokens, and Plex tokens stored in `managerr.db` are encrypted with AES-256-GCM. A database backup without `master.key` (or an explicit `MANAGERR_MASTER_KEY`) cannot decrypt stored secrets.

### Method 1: UI Online Backup
Navigate to **System -> Backup** in the web UI and click **Backup Now**. Managerr executes an atomic `VACUUM INTO '/config/backups/managerr_backup_<timestamp>.db'` call. The snapshot remains consistent while background writes occur. You can also configure the maximum number of retained backup files under **System -> Backup** (`GET/PUT /api/v1/system/backups/settings`).

### Method 2: Offline Filesystem Snapshot
Stop the container and archive the config directory:

```bash
docker compose stop managerr
tar -czvf managerr-backup-$(date +%F).tar.gz -C ./appdata config
docker compose start managerr
```

### Restoration Procedure
1. Stop the running container: `docker compose down`.
2. Extract the backup archive into your host config path (`./appdata/config`).
3. Ensure ownership matches your configured `PUID:PGID`:
   ```bash
   sudo chown -R 1000:1000 ./appdata/config
   ```
4. Verify `master.key` is present in `/config`.
5. Start the container: `docker compose up -d`.

---

## 11. Reverse Proxy, Forwarded Headers & HTTPS Configuration

Managerr listens on plain HTTP (`port 8989`). In production, place Managerr behind a reverse proxy (Caddy, Traefik, or Nginx) to handle TLS termination.

### Forwarded Headers & Trusted Proxy CIDRs
When running behind a reverse proxy or overlay network (such as Tailscale or a Docker bridge proxy), enable forwarded header processing so Managerr resolves real client IP addresses (for rate limiting, audit logs, and API key CIDR checks), HTTPS scheme (`X-Forwarded-Proto`) for OIDC/Passkey callbacks, and original host headers:

* **UI Configuration:** Navigate to **Settings -> Security & Auth** (`/settings/general`) and configure **Trust Forwarded Headers**, **Trusted Proxy CIDRs**, and **Allowed Proxy Hosts**.
* **Environment Variables:**
  ```ini
  Managerr__Auth__TrustForwardedHeaders=true
  Managerr__Auth__TrustedProxyCidrs__0=127.0.0.1/32
  Managerr__Auth__TrustedProxyCidrs__1=172.16.0.0/12
  Managerr__Auth__TrustedProxyCidrs__2=100.64.0.0/10
  Managerr__Auth__AllowedHosts__0=managerr.example.com
  ```
* **Tailscale Tip:** Tailscale assigns client and node IPv4 addresses from the `100.64.0.0/10` CGNAT range. Adding `100.64.0.0/10` to both `Managerr__Auth__TrustedProxyCidrs` (for reverse proxies over Tailscale) and `SafeHttpEgress__AllowedPrivateCidrs` (if Managerr connects to Plex or download clients over Tailscale) prevents SSRF blocks and proxy IP spoofing rejections.

### Nginx Example
```nginx
server {
    listen 443 ssl http2;
    server_name managerr.example.com;

    ssl_certificate /etc/letsencrypt/live/example.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/example.com/privkey.pem;

    location / {
        proxy_pass http://127.0.0.1:8989;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_read_timeout 300s;
    }
}
```

### Caddy Example
```caddy
managerr.example.com {
    reverse_proxy 127.0.0.1:8989
}
```
