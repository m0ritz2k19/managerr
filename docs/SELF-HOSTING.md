# Running your own Managerr

This guide is for anyone running a self-hosted Managerr instance. Managerr runs as a single Docker container; its databases are embedded (SQLite), so no external database services are needed.

**You need:**

- A Linux host with **Docker** and the **Docker Compose plugin** (`docker compose version` works).
- **Plex Media Server** (recommended if you want Plex SSO, shared library RBAC, or Cleanerr watch-state syncing; local accounts and OIDC are also supported).
- A **download client**: one of the 12 supported Torrent or Usenet clients (see [Download clients](#5-download-clients)).
- At least one **indexer** that speaks the Newznab (Usenet) or Torznab (torrent) API, or custom Cardigann v12 YAML definitions. Managerr ships no indexer or tracker definitions; you add your own.

> **Full 4-Service Test Stack (`Managerr` + `Seerr` + `SABnzbd` + `Plex`):**
> If you want to spin up an all-in-one stack with Managerr alongside Seerr, SABnzbd, and Plex on a shared `/data` volume, see [`deploy/test-stack/README.md`](../deploy/test-stack/README.md).

## 1. Get the code or prebuilt image

Clone the repository:

```bash
git clone https://github.com/m0ritz2k19/managerr.git
cd managerr
```

The `upstream/` submodules are reference code only; you do not need them to run Managerr.

Prebuilt container images are also published to GitHub Container Registry:

```text
ghcr.io/m0ritz2k19/managerr:latest
```

To run the prebuilt GHCR image instead of building from source, set `MANAGERR_IMAGE=ghcr.io/m0ritz2k19/managerr:latest` in `.env` and start with `docker compose up -d`.

## 2. Pick your folders

Managerr needs two folders on the host:

| Setting | What it is | In the container |
|---|---|---|
| `DATA_DIR` | One folder that holds **both** your downloads and your media libraries | `/data` |
| `CONFIG_DIR` | Managerr's databases and its master key (default `./appdata/config` in the checkout) | `/config` |

Keep downloads and media under the same `DATA_DIR`, on the same filesystem. Managerr can then import by hardlinking (instant, no extra space) instead of copying. A layout that works:

```text
/srv/data
├── downloads          # your download client saves here
└── media
    ├── movies
    ├── tv
    ├── music
    └── books
```

## 3. Install and start

The setup script checks Docker, writes `.env`, creates the folders, builds or pulls the image, and waits until Managerr answers:

```bash
scripts/selfhost-setup.sh --data-dir /srv/data
```

It runs the container as the owner of `DATA_DIR` and takes the host's time zone. Use `--port 8990` for another port.
Running it again keeps your `.env`.

Prefer to do it by hand?

```bash
cp .env.example .env    # set DATA_DIR, check PUID/PGID (owner of DATA_DIR: id -u / id -g)
docker compose up -d --build
docker compose logs -f managerr
```

Building from source takes a few minutes (it builds the .NET backend and the web UI). When ready, Managerr listens on `http://<host>:8989`, and `http://<host>:8989/api/v1/system/health` returns `{"status":"healthy",...}`.

## 4. First sign-in and Authentication

Managerr supports multi-provider authentication: **Local Accounts**, **Plex**, **OpenID Connect (OIDC 1.0 + PKCE)**, and **Passkeys (WebAuthn)**.

### First Sign-in Options

When launching a new Managerr instance for the first time, you have several options to claim instance ownership:

1. **Local Account Setup (Web UI)**:
   - Open `http://<host>:8989`.
   - Complete the initial setup wizard by creating a local username, password, and email. The first local user becomes the instance owner and admin.
2. **Plex Sign-in**:
   - Click "Sign in with Plex". The Plex account that owns the bound Plex Media Server claims the instance and becomes its owner.
3. **Headless Admin Bootstrap (Environment / Docker Secrets)**:
   - Set `Managerr__Auth__Local__BootstrapAdmin__Username=admin` and `Managerr__Auth__Local__BootstrapAdmin__Password=...` (or use `Managerr__Auth__Local__BootstrapAdmin__Password_FILE=/run/secrets/admin_pass`).
   - On container startup, Managerr automatically provisions the initial admin/owner without requiring manual UI setup.
4. **OpenID Connect (OIDC)**:
   - When configured, logging in with an OIDC account matching your configured `AdminGroups` automatically claims ownership if no owner exists yet.

---

### Authentication Providers & Security Features

#### Local Accounts & Break-Glass Login
- Local accounts allow signing into Managerr directly without relying on external cloud authentication services.
- Admins can provision additional local users under **Settings -> Users** (or **System -> Users & Roles**).
- Each user can manage their profile and change their password from the user profile modal.
- If Plex or your SSO provider goes down, local credentials provide immediate break-glass access.

#### Passkeys / WebAuthn
- Managerr supports native FIDO2/WebAuthn passkeys (Touch ID, Face ID, Windows Hello, YubiKeys, Google Titan, 1Password, Bitwarden).
- Users can register and manage passkeys from **System -> Security** or the **user profile modal**.
- Relying Party (`rp_id`, `rp_name`, `require_user_verification`) settings can be configured under **Settings -> General**.
- Passkey login offers phishing-resistant, passwordless authentication with no shared passwords.

#### OpenID Connect (OIDC 1.0 + PKCE)
- Compatible with standard OIDC identity providers: **Authentik**, **Keycloak**, **Authelia**, **Okta**, **GitLab**, etc.
- Uses PKCE (Proof Key for Code Exchange with SHA-256) and signed JWT tokens (`RS256`, `RS384`, `RS512`, `ES256`, `ES384`) with JWKS verification.
- **Claim & Role Group Mapping:** Configure `username`, `email`, `displayName`, and `groups` claim names along with admin/manager/requester group mappings directly in **Settings -> General** or via environment variables:
  - `Managerr__Auth__Oidc__AdminGroups__0=managerr-admins`
  - `Managerr__Auth__Oidc__ManagerGroups__0=managerr-managers`
  - `Managerr__Auth__Oidc__RequesterGroups__0=managerr-users`
- **Auto-Provisioning:** New users authenticated via OIDC can be automatically created on first login.

---

### Configuration: Environment, JSON, and Docker Secrets

Managerr supports four tiers of configuration:

1. **Docker Secrets & `*_FILE` Convention**:
   - Any environment variable ending with `_FILE` causes Managerr to read the secret from the referenced file path (e.g. `/run/secrets/...`).
   - Example: `Managerr__Auth__Oidc__ClientSecret_FILE=/run/secrets/oidc_secret`
   - Example: `Managerr__MasterKey_FILE=/run/secrets/master_key`
2. **Environment Variables**:
   - Hierarchical configuration keys use double underscores (`__`) to delimit sections.
   - Example: `Managerr__Auth__Oidc__Enabled=true`
3. **Optional Configuration File (`/config/managerr.json`)**:
   - You may place a `managerr.json` file in your config directory to configure auth or UI defaults.
4. **Settings UI**:
   - Settings defined via environment variables or Docker secrets are locked and marked as managed by `environment` in the Web UI to prevent accidental overwrites. Settings not defined by environment or files can be configured directly from **Settings -> General**.

## 5. Download clients

**Settings -> Download Clients -> Add.** All 12 supported download clients integrate with queue tracking, category routing, and import orchestration: **qBittorrent**, **Deluge**, **Transmission**, **rTorrent**, **Flood**, **Vuze**, **aria2**, **SABnzbd**, **NZBGet**, **Pneumatic**, **torrent_blackhole**, and **usenet_blackhole**.

The important part is **paths**: Managerr has to find the finished download at a path inside its own container.

- **Easiest:** the download client also mounts the same `DATA_DIR`, as `/data`, and saves to `/data/downloads/...`. Then the paths match and nothing else is needed.
- **Different paths** (other host, other mount point): configure a **Remote Path Mapping**, for example `/downloads` as the client reports it -> `/data/downloads` in Managerr. You can add and edit mappings directly in the UI under **Settings -> Download Clients -> Remote Path Mappings**, or via the API (`/api/v1/downloaders/remote-paths`) using an API key with the `downloaders:write` scope:

  ```bash
  curl -X POST http://<host>:8989/api/v1/downloaders/remote-paths \
    -H "X-Api-Key: <your key>" -H "Content-Type: application/json" \
    -d '{"downloadClientId": null, "host": "<download client host>", "remotePath": "/downloads", "localPath": "/data/downloads"}'
  ```

  `downloadClientId: null` applies the mapping to every client on that host; `GET` on the same URL lists them.

Categories: Managerr sends each grab with a category per media type (`tv-managerr`, `movies-managerr`, ...). Create these in your download client, or change the mapping in the client's settings in Managerr.

qBittorrent: enter the WebUI username and password unless you allow login-free access from Managerr's address.

## 6. Indexers

**Settings -> Indexers -> Add**, then **Newznab** for Usenet indexers or **Torznab** for torrent indexers (or an aggregator like Prowlarr or Jackett). Enter the indexer's API URL and your API key and use **Test**.

Cardigann definitions are supported only as YAML files you provide: put them into `CONFIG_DIR/Definitions/Custom` and they appear under "Your Cardigann Definitions".

## 7. Libraries and profiles

1. **Settings -> Media Management:** add a library per media type with a root folder under `/data/media/...` (e.g. `/data/media/movies`). If you use Plex, bind each library to its Plex section so Plex refreshes after imports.
2. **Settings -> Profiles:** the built-in quality profiles are a starting point; pick one per library.
3. **Library -> Add New:** search a title and add it. Managerr searches your indexers, sends the best release to your download client and imports it when it is done (**Activity -> Queue** shows progress).

## 8. Cleanerr Lifecycle Studio & Discord Bot

Managerr includes an integrated **Cleanerr** media lifecycle engine (**Cleanerr -> Studio / Queue / Exclusions**):

1. **Workflows & Rule Builder:** Create cleanup rules using the visual DAG Workflow Studio or the Maintainerr-compatible Rule Builder (`AND`/`OR` rule groups, YAML import/export). Run dry-run simulations before enabling live runs.
2. **Whole-Series Handling & Leaving Soon Queue:** TV shows are evaluated as whole series. Matched items are staged in Managerr's native **Leaving Soon** queue without modifying Plex collections, and hardlink checks prevent deleting files still linked elsewhere.
3. **Discord Bot Integration:** Under Cleanerr Discord settings, configure your webhook URL or **Discord Bot Token**, **Channel ID**, **Application ID**, **Public Key**, and optional **Guild ID** / **Public Base URL** to enable `/cleanerr` slash commands, interactive Rescue/Exclude/Keep buttons, and HMAC-signed magic links.

## 9. Optional: Seerr / Overseerr

Managerr can stand in for Sonarr and Radarr in Seerr:

1. Copy the instance API key from **Settings -> General -> Security -> API Key** (just like Sonarr/Radarr), or create a scoped key under **System -> Security -> Add API Key** with the Seerr preset (scopes `compat:sonarr` and `compat:radarr`).
2. In Seerr, add a **Radarr** server: host and port of Managerr, URL base **`/compat/radarr`**, the key. Add a **Sonarr** server the same way with URL base **`/compat/sonarr`**. Use **Test** in Seerr, then pick profile and root folder.

> [!TIP]
> **Anime season alignment:** Seerr v3.5+ supports TVDB metadata for anime under **Settings -> Metadata -> Anime -> TheTVDB** (paired with Plex TV library **Episode ordering: TheTVDB (Aired)**). This ensures multi-cour anime seasons align 1:1 across Seerr, Managerr, Usenet/Torrents, and Plex. If Seerr remains configured with TMDB (which collapses multi-season anime into a single season), Managerr automatically detects the TMDB single-season collapse upon request and expands monitoring across all corresponding TVDB seasons; you can also force expansion via the setting `compat.sonarr.expand_single_season_anime = true`.

## 10. Updating

If building locally:

```bash
git pull
docker compose up -d --build
```

If using the prebuilt GHCR image (`ghcr.io/m0ritz2k19/managerr:latest`):

```bash
docker compose pull
docker compose up -d
```

Database migrations run automatically on start.

## 11. Backups

Back up **`CONFIG_DIR` as a whole**, ideally while the container is stopped (`docker compose stop`), or use **System -> Backup** in the UI (non-blocking SQLite `VACUUM INTO`). It contains:

- `managerr.db` and the other `*.db` files: your library, settings, history;
- **`master.key`**: encrypts the stored secrets (download client passwords, API keys). A database backup without this file cannot decrypt them. Keep it as private as the passwords themselves.

To restore, put the folder back and start the container.

## Behind a reverse proxy & Tailscale

Managerr speaks plain HTTP on port `8989` and works behind Traefik, Caddy, or nginx; point the proxy at that port and terminate TLS there. If the proxy runs in Docker, you can drop the `ports:` section and attach the container to the proxy's network instead (or bind `MANAGERR_BIND_IP=127.0.0.1` on the host).

- **Forwarded Headers (`X-Forwarded-*`):** Forwarded header trust is disabled by default (`TrustForwardedHeaders=false`) to prevent IP spoofing. Enable **Trust Forwarded Headers** under **Settings -> General** (or set `Managerr__Auth__TrustForwardedHeaders=true`), configure at least one entry in `TrustedProxyCidrs` to restrict which proxy IPs/subnets are trusted, and optionally set `AllowedHosts` to allowlist `X-Forwarded-Host` values.
- **Tailscale / CGNAT Egress:** If your download clients, Plex server, or indexers sit on a Tailscale network (`100.64.0.0/10` CGNAT range), ensure `100.64.0.0/10` is included in `SafeHttpEgress__AllowedPrivateCidrs__*` if you customize outbound allowlists.

## Troubleshooting

| Problem | Check |
|---|---|
| Container restarts, logs say it cannot write to `/config` | `CONFIG_DIR` must belong to `PUID:PGID` from `.env`: `sudo chown -R <PUID>:<PGID> <CONFIG_DIR>` |
| Downloads finish but are never imported | The download's path must exist inside the container: compare **Activity -> Queue** with your download client and add a remote path mapping in **Settings -> Download Clients -> Remote Path Mappings** ([section 5](#5-download-clients)) |
| Imports copy instead of hardlinking | Downloads and media are on different filesystems or different mounts; keep both under one `DATA_DIR` |
| A download client or indexer on your LAN or Tailscale is refused | Managerr blocks requests to cloud metadata addresses; private LAN addresses are allowed by default. If you set `SafeHttpEgress__AllowedPrivateCidrs__*`, include your LAN range and Tailscale `100.64.0.0/10` if used |
| Plex sign-in loops or fails | The Plex account must own the server or be a member of it; use a local account or Passkey meanwhile |

Logs: `docker compose logs -f managerr`. Health: `curl http://localhost:8989/api/v1/system/health`.
