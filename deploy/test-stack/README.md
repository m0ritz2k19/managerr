# Managerr Full Test Stack (`Managerr` + `Seerr` + `SABnzbd` + `Plex`)

This folder provides a ready-to-run 4-service Docker Compose stack that builds **Managerr** from this checkout and runs it alongside **Seerr**, **SABnzbd**, and **Plex Media Server**.

All containers share a single `/data` bind mount on the same filesystem so **atomic moves (`atomic_move`)** and **Unix hardlinks** work instantaneously with zero copy overhead and no remote path mappings required.

---

## 1. Quick Start

### Step 1: Create `.env` and Folders

```bash
cd deploy/test-stack
cp .env.example .env
```

Edit `.env` and set at least:
- `MANAGERR_ADMIN_PASS`: Initial local admin password for Managerr (`http://<host>:8989`).
- `PUID` / `PGID`: Your Linux user/group ID (`id -u` and `id -g`).
- `PLEX_CLAIM` *(optional)*: A 4-minute claim token from <https://www.plex.tv/claim> so Plex claims your server on first boot.

Create the host directories owned by your user:

```bash
mkdir -p \
  appdata/{managerr,sabnzbd,seerr,plex} \
  data/downloads/{incomplete,complete} \
  data/media/{movies,tv,anime,music,books}
```

### Step 2: Launch the Stack

The first start builds the Managerr image from this checkout, which takes a few minutes. To run a prebuilt image instead, set `MANAGERR_IMAGE` in `.env` and start with `--no-build`.

```bash
docker compose up -d --build
```

Check container status and health:

```bash
docker compose ps
curl -fsS http://localhost:8989/api/v1/system/health
```

---

## 2. Web UIs & Default Ports

| Service | Host URL | Internal Container URL | Purpose |
| :--- | :--- | :--- | :--- |
| **Managerr** | `http://<host>:8989` | `http://managerr:8989` | Unified catalog, indexers, download orchestration & verification |
| **Seerr** | `http://<host>:5055` | `http://seerr:5055` | Media discovery & requests UI (talks to Managerr via `/compat/*`) |
| **SABnzbd** | `http://<host>:8080` | `http://sabnzbd:8080` | Usenet download client |
| **Plex** | `http://<host>:32400/web` | `http://plex:32400` | Media server |

---

## 3. Wiring the Services Together

Because all four services run on the `managerr-stack` bridge network and mount `${DATA_DIR}` at `/data`, use the internal container hostnames and `/data/...` paths below:

### A. SABnzbd (`http://<host>:8080`)
1. Complete the SABnzbd quick-start wizard and add your Usenet server under **Settings -> Servers**.
2. Go to **Settings -> Folders** and set:
   - **Temporary Download Folder**: `/data/downloads/incomplete`
   - **Completed Download Folder**: `/data/downloads/complete`
3. Under **Settings -> Categories**, add categories matching Managerr (`movies-managerr`, `tv-managerr`, `anime-managerr`, `music-managerr`, `books-managerr`) or leave default (`*`).
4. Copy the **API Key** from **Settings -> General**.

### B. Plex (`http://<host>:32400/web`)
1. Open `http://<host>:32400/web`, sign in, and create your media libraries under **Settings -> Manage -> Libraries**:
   - **Movies**: `/data/media/movies`
   - **TV Shows**: `/data/media/tv`

### C. Managerr (`http://<host>:8989`)
1. Sign in using either **Sign in with Plex** or the local bootstrap credentials (`MANAGERR_ADMIN_USER` / `MANAGERR_ADMIN_PASS` from `.env`).
2. **Libraries (`Settings -> Media Management`)**:
   - Add a **Movies** library at `/data/media/movies` and a **TV** library at `/data/media/tv`.
   - Import mode defaults to **Move (`atomic_move`)**, which moves verified downloads instantaneously from `/data/downloads/complete` into `/data/media/...` on the same `/data` mount.
3. **Download Client (`Settings -> Download Clients -> Add -> SABnzbd`)**:
   - **Host**: `sabnzbd`
   - **Port**: `8080`
   - **API Key**: *(paste from SABnzbd)*
   - Click **Test** and **Save**.
4. **Indexers (`Settings -> Indexers -> Add`)**:
   - Add your **Newznab** or **Torznab** indexers with their API URL and API key.

### D. Seerr (`http://<host>:5055`)
1. In Managerr, copy the instance API key directly from **Settings -> General -> Security -> API Key** (just like Sonarr/Radarr), or optionally mint a least-privilege key under **System -> Security -> Add API Key** with the **Seerr** preset (grants `compat:radarr` and `compat:sonarr` scopes).
2. In Seerr (**Settings -> Services**):
   - **Add Radarr Server**:
     - **Hostname / IP**: `managerr`
     - **Port**: `8989`
     - **URL Base**: `/compat/radarr`
     - **API Key**: *(paste Managerr API key)*
     - Click **Test**, select a Quality Profile and `/data/media/movies` root folder, and enable **Enable Scan**.
   - **Add Sonarr Server**:
     - **Hostname / IP**: `managerr`
     - **Port**: `8989`
     - **URL Base**: `/compat/sonarr`
     - **API Key**: *(paste Managerr API key)*
     - Click **Test**, select a Quality Profile and `/data/media/tv` root folder, and enable **Enable Scan**.

---

## 4. Instant Download Import & Real-Time Release Push (Optional)

Managerr automatically switches its download queue monitor to **5-second fast-polling** whenever an item is actively downloading or unpacking in SABnzbd.

If you also want **zero-second webhook notification** the instant SABnzbd finishes unpacking or an indexer announces a release:

- **SABnzbd On-Complete Webhook**:
  Create an API key in Managerr with scope `queue:manage` (or use your Sonarr/Radarr compat key) and trigger:
  ```bash
  curl -X POST "http://managerr:8989/api/v1/downloaders/notify" \
    -H "X-Api-Key: <MANAGERR_API_KEY>"
  ```
  *(Sonarr/Radarr `DownloadedEpisodesScan` / `DownloadedMoviesScan` commands on `/compat/{sonarr,radarr}/api/v3/command` also trigger an immediate download check and import.)*

- **Real-Time Indexer / IRC Release Push (`autobrr` / Newznab Push)**:
  Push newly announced releases directly to Managerr for immediate evaluation against monitored series/movies without waiting for the scheduled RSS sync timer:
  - Native API (`indexers:search` scope): `POST http://managerr:8989/api/v1/indexers/release/push`
  - Sonarr/Radarr v3 Compat (`compat:sonarr` / `compat:radarr` scope): `POST http://managerr:8989/compat/sonarr/api/v3/release/push` or `POST http://managerr:8989/compat/radarr/api/v3/release/push`

---

## 5. Updating

To update, pull the newest code and rebuild the Managerr container:

```bash
git pull
docker compose up -d --build managerr
```
