#!/usr/bin/env bash
# Set up and start your own Managerr instance with Docker Compose. Safe to run again: an existing .env is kept.
#
#   scripts/selfhost-setup.sh                         # asks for the data folder
#   scripts/selfhost-setup.sh --data-dir /srv/data    # no questions
#   scripts/selfhost-setup.sh --data-dir /srv/data --port 8990 --no-start
#
# Guide: docs/SELF-HOSTING.md
set -euo pipefail

usage() {
  sed -n '2,8p' "$0" | sed 's/^# \{0,1\}//'
  exit "${1:-0}"
}

DATA_DIR_ARG=""
PORT_ARG=""
START=1
while [[ $# -gt 0 ]]; do
  case "$1" in
    --data-dir) DATA_DIR_ARG="${2:-}"; shift 2 ;;
    --port) PORT_ARG="${2:-}"; shift 2 ;;
    --no-start) START=0; shift ;;
    -h|--help) usage 0 ;;
    *) echo "Unknown option: $1" >&2; usage 2 ;;
  esac
done

cd "$(dirname "$0")/.."
say() { printf '\033[1m%s\033[0m\n' "$*"; }
fail() { printf 'Error: %s\n' "$*" >&2; exit 1; }

# 1. Docker
command -v docker >/dev/null || fail "Docker is not installed (https://docs.docker.com/engine/install/)."
docker compose version >/dev/null 2>&1 || fail "Docker Compose v2 is missing ('docker compose ...'); install the compose plugin."
if [[ $START -eq 1 ]] && ! docker info >/dev/null 2>&1; then
  fail "Cannot talk to the Docker daemon. Is it running, and is your user in the 'docker' group? (sudo usermod -aG docker \$USER, then log in again)"
fi

# 2. .env
set_env() { # key value: replace the line or append it
  if grep -q "^$1=" .env; then sed -i "s|^$1=.*|$1=$2|" .env; else printf '%s=%s\n' "$1" "$2" >> .env; fi
}
get_env() { grep -E "^$1=" .env | tail -n1 | cut -d= -f2-; }

if [[ ! -f .env ]]; then
  say "Creating .env from .env.example"
  cp .env.example .env

  data_dir="$DATA_DIR_ARG"
  if [[ -z "$data_dir" ]]; then
    [[ -t 0 ]] || fail "No --data-dir given and no terminal to ask on."
    read -r -p "Folder that holds your downloads and media (mounted as /data): " data_dir
  fi
  [[ -n "$data_dir" ]] || fail "A data folder is required."
  data_dir="$(realpath -m "$data_dir")"
  if [[ ! -d "$data_dir" ]]; then
    say "Creating $data_dir with downloads/ and media/{movies,tv,music,books}"
    mkdir -p "$data_dir"/downloads "$data_dir"/media/{movies,tv,music,books}
  fi
  set_env DATA_DIR "$data_dir"

  # Run as the owner of the data folder, so imported files belong to them
  set_env PUID "$(stat -c %u "$data_dir")"
  set_env PGID "$(stat -c %g "$data_dir")"

  tz="$(timedatectl show -p Timezone --value 2>/dev/null || cat /etc/timezone 2>/dev/null || true)"
  [[ -n "$tz" ]] && set_env TZ "$tz"
  [[ -n "$PORT_ARG" ]] && set_env MANAGERR_PORT "$PORT_ARG"
else
  say "Keeping the existing .env"
  [[ -n "$DATA_DIR_ARG" || -n "$PORT_ARG" ]] && echo "  (--data-dir/--port only apply when .env is created; edit .env to change them)"
fi

[[ -n "$(get_env DATA_DIR)" ]] || fail "DATA_DIR is empty in .env."
[[ -d "$(get_env DATA_DIR)" ]] || fail "DATA_DIR $(get_env DATA_DIR) does not exist."

# 3. Config folder, owned by the container user
config_dir="$(get_env CONFIG_DIR)"; config_dir="${config_dir:-./appdata/config}"
mkdir -p "$config_dir"
puid="$(get_env PUID)"
if [[ "$(stat -c %u "$config_dir")" != "$puid" ]]; then
  echo "Warning: $config_dir is not owned by PUID $puid; Managerr may not be able to write to it." >&2
  echo "         Fix with: sudo chown -R $puid:$(get_env PGID) $config_dir" >&2
fi

echo
say "Settings (.env)"
grep -E '^(DATA_DIR|CONFIG_DIR|PUID|PGID|MANAGERR_PORT|TZ)=' .env | sed 's/^/  /'

[[ $START -eq 1 ]] || { echo; say "Not started (--no-start). Start with: docker compose up -d --build"; exit 0; }

# 4. Build and start
echo
say "Building and starting Managerr (the first build takes a few minutes)"
docker compose up -d --build

port="$(get_env MANAGERR_PORT)"; port="${port:-8989}"
say "Waiting for Managerr to answer on port $port"
for _ in $(seq 1 60); do
  if curl -fsS "http://127.0.0.1:$port/api/v1/system/health" >/dev/null 2>&1; then
    host="$(hostname -I 2>/dev/null | awk '{print $1}')"
    echo
    say "Managerr is running: http://${host:-localhost}:$port"
    cat <<EOF

Next steps (details in docs/SELF-HOSTING.md):
  1. Open the URL and sign in with the Plex account that owns your Plex server.
  2. Settings > Download clients: add yours; its download folder must be under /data/downloads.
  3. Settings > Indexers: add your Newznab/Torznab indexers.
  4. Add libraries with root folders under /data/media/...

Logs: docker compose logs -f managerr
EOF
    exit 0
  fi
  sleep 5
done

fail "Managerr did not become healthy within 5 minutes. Check: docker compose logs managerr"
