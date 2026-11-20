#!/usr/bin/env bash
# Set up and start your own Managerr instance with Docker Compose. Safe to run again: an existing .env is kept.
#
#   scripts/selfhost-setup.sh                         # asks for the data folder and pulls GHCR image
#   scripts/selfhost-setup.sh --data-dir /srv/data    # no questions
#   scripts/selfhost-setup.sh --data-dir /srv/data --build --port 8990 --no-start
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
BUILD_FROM_SOURCE=0

say() { printf '\033[1m%s\033[0m\n' "$*"; }
fail() { printf 'Error: %s\n' "$*" >&2; exit 1; }
validate_port() {
  [[ "$1" =~ ^[0-9]+$ ]] && (( 1 <= 10#$1 && 10#$1 <= 65535 )) || fail "Invalid port '$1' (expected 1-65535)."
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --data-dir)
      [[ $# -ge 2 ]] || fail "--data-dir requires a value."
      DATA_DIR_ARG="$2"
      shift 2
      ;;
    --port)
      [[ $# -ge 2 ]] || fail "--port requires a value."
      validate_port "$2"
      PORT_ARG="$2"
      shift 2
      ;;
    --build) BUILD_FROM_SOURCE=1; shift ;;
    --no-start) START=0; shift ;;
    -h|--help) usage 0 ;;
    *) echo "Unknown option: $1" >&2; usage 2 ;;
  esac
done

cd "$(dirname "$0")/.."

# 1. Docker
command -v docker >/dev/null || fail "Docker is not installed (https://docs.docker.com/engine/install/)."
docker compose version >/dev/null 2>&1 || fail "Docker Compose v2 is missing ('docker compose ...'); install the compose plugin."
if [[ $START -eq 1 ]] && ! docker info >/dev/null 2>&1; then
  fail "Cannot talk to the Docker daemon. Is it running, and is your user in the 'docker' group? (sudo usermod -aG docker \$USER, then log in again)"
fi

# 2. .env
set_env() { # key value: replace the line or append it without sed/newline injection
  local key="$1" val="$2" tmp
  [[ "$val" != *$'\n'* && "$val" != *$'\r'* ]] || fail "Invalid control character in $key."
  if [[ "$val" =~ [[:space:]\$\#] ]]; then
    [[ "$val" != *"'"* ]] || fail "Value for $key cannot contain single quotes alongside spaces or special characters."
    val="'$val'"
  fi
  tmp="$(umask 077 && mktemp .env.tmp.XXXXXX)"
  VAL="$val" awk -v k="$key" '
    BEGIN { v = ENVIRON["VAL"]; prefix = k "="; len = length(prefix); found = 0 }
    substr($0, 1, len) == prefix { print prefix v; found = 1; next }
    { print }
    END { if (!found) print prefix v }
  ' .env > "$tmp" && chmod 600 "$tmp" && mv "$tmp" .env
}

get_env() {
  local v
  v="$({ grep -E "^$1=" .env || true; } | tail -n1 | cut -d= -f2-)"
  v="${v%\"}"; v="${v#\"}"
  v="${v%\'}"; v="${v#\'}"
  printf '%s' "$v"
}

if [[ ! -f .env ]]; then
  say "Creating .env from .env.example"
  install -m 600 .env.example .env

  data_dir="$DATA_DIR_ARG"
  if [[ -z "$data_dir" ]]; then
    [[ -t 0 ]] || fail "No --data-dir given and no terminal to ask on."
    read -r -p "Folder that holds your downloads and media (mounted as /data): " data_dir
  fi
  [[ -n "$data_dir" ]] || fail "A data folder is required."
  data_dir="$(realpath -m -- "$data_dir")"
  if [[ ! -d "$data_dir" ]]; then
    say "Creating $data_dir with downloads/ and media/{movies,tv,music,books}"
    mkdir -p -- "$data_dir"/downloads "$data_dir"/media/{movies,tv,music,books}
  fi
  set_env DATA_DIR "$data_dir"

  # Run as the owner of the data folder, so imported files belong to them
  uid="$(stat -c %u -- "$data_dir")"
  gid="$(stat -c %g -- "$data_dir")"
  if [[ "$uid" -eq 0 || "$gid" -eq 0 ]]; then
    fail "$data_dir is owned by root (0:0). Refusing to run the container as root; chown $data_dir to a non-root user first."
  fi
  set_env PUID "$uid"
  set_env PGID "$gid"

  tz="$(timedatectl show -p Timezone --value 2>/dev/null || cat /etc/timezone 2>/dev/null || true)"
  [[ -n "$tz" ]] && set_env TZ "$tz"
  [[ -n "$PORT_ARG" ]] && set_env MANAGERR_PORT "$PORT_ARG"
else
  say "Keeping the existing .env"
  [[ -n "$DATA_DIR_ARG" || -n "$PORT_ARG" ]] && echo "  (--data-dir/--port only apply when .env is created; edit .env to change them)"
fi

data_dir_cfg="$(get_env DATA_DIR)"
[[ -n "$data_dir_cfg" ]] || fail "DATA_DIR is empty in .env."
[[ -d "$data_dir_cfg" ]] || fail "DATA_DIR $data_dir_cfg does not exist."

# 3. Config folder, owned by the container user
config_dir="$(get_env CONFIG_DIR)"; config_dir="${config_dir:-./appdata/config}"
mkdir -p -- "$config_dir"
puid="$(get_env PUID)"; puid="${puid:-1000}"
pgid="$(get_env PGID)"; pgid="${pgid:-1000}"
if [[ "$(stat -c %u -- "$config_dir")" != "$puid" ]]; then
  echo "Warning: $config_dir is not owned by PUID $puid; Managerr may not be able to write to it." >&2
  echo "         Fix with: sudo chown -R $puid:$pgid \"$config_dir\"" >&2
fi

echo
say "Settings (.env)"
grep -E '^(DATA_DIR|CONFIG_DIR|PUID|PGID|MANAGERR_PORT|TZ)=' .env | sed 's/^/  /'

if [[ $START -eq 0 ]]; then
  echo
  if [[ $BUILD_FROM_SOURCE -eq 1 ]]; then
    say "Not started (--no-start). Build and start with: docker compose -f compose.yaml -f compose.build.yaml up -d --build"
  else
    say "Not started (--no-start). Start with: docker compose up -d"
  fi
  exit 0
fi

# 4. Pull or build and start
echo
if [[ $BUILD_FROM_SOURCE -eq 1 ]]; then
  say "Building from source and starting Managerr (the first build takes a few minutes)"
  docker compose -f compose.yaml -f compose.build.yaml up -d --build
else
  say "Pulling prebuilt image and starting Managerr"
  docker compose pull
  docker compose up -d
fi

port="$(get_env MANAGERR_PORT)"; port="${port:-8989}"
validate_port "$port"
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
