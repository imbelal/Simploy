#!/usr/bin/env bash
#
# Simploy Agent one-liner installer (Ubuntu/Debian + Docker).
#
#   curl -fsSL https://<your-control-plane>/api/servers/install | bash
#
# or locally:
#   SIMPLOY_REPO=/path/to/Simploy bash scripts/install.sh
#
# The agent listens on :8089, builds/runs your apps via docker compose,
# and opens /opt/simploy for project files. It must run on the VM you want
# to deploy apps to. Add that VM's IP as a Server in the Simploy UI.

set -euo pipefail

SIMPLOY_REPO="${SIMPLOY_REPO:-https://github.com/imbelal/Simploy}"
AGENT_PORT="${AGENT_PORT:-8089}"
INSTALL_DIR="${INSTALL_DIR:-/opt/simploy}"
WORK="${WORK:-/tmp/simploy-install}"
AGENT_CONTAINER="simploy-agent"
# Must match the api service's Agent__Token in docker-compose.yml.
AGENT_TOKEN="${AGENT_TOKEN:-${SIMPLOY_AGENT_TOKEN:-simploy-agent-token-change-me}}"

info(){ printf '\033[0;36m[simploy]\033[0m %s\n' "$*"; }
die(){ printf '\033[0;31m[simploy]\033[0m ERROR: %s\n' "$*" >&2; exit 1; }

# Ubuntu vs Debian codename helper for the docker apt repo
dist_codename() {
  (. /etc/os-release && echo "$ID" && echo "$VERSION_CODENAME")
}

info "Simploy Agent installer"
info "repo=${SIMPLOY_REPO}  port=${AGENT_PORT}  dir=${INSTALL_DIR}"

# ---- 1. Docker -------------------------------------------------------------
if ! command -v docker >/dev/null 2>&1; then
  info "Docker not found - installing via apt..."
  command -v apt-get >/dev/null 2>&1 || die "Unsupported distro (needs apt/Docker)."
  apt-get update -y
  apt-get install -y ca-certificates curl gnupg
  install -m 0755 -d /etc/apt/keyrings
  curl -fsSL https://download.docker.com/linux/"$(dist_codename | sed -n 2p)"/gpg \
    -o /etc/apt/keyrings/docker.asc 2>/dev/null \
    || curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
  chmod a+r /etc/apt/keyrings/docker.asc
  . /etc/os-release >/dev/null 2>&1 || true
  DIST_ID=$(dist_codename | sed -n 1p)
  CODENAME=$(dist_codename | sed -n 2p)
  echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/${DIST_ID} ${CODENAME:-stable} stable" \
    > /etc/apt/sources.list.d/docker.list
  apt-get update -y
  apt-get install -y docker-ce docker-ce-cli containerd.io docker-compose-plugin
fi
systemctl enable --now docker 2>/dev/null || true

# ---- 2. Fetch source ------------------------------------------------------
info "Fetching Simploy (${SIMPLOY_REPO})..."
rm -rf "$WORK"
if [[ -d "$SIMPLOY_REPO" ]]; then
  git clone --depth 1 "$SIMPLOY_REPO" "$WORK"
else
  git clone --depth 1 "$SIMPLOY_REPO" "$WORK"
fi
[[ -f "$WORK/src/Simploy.Agent/Simploy.Agent.csproj" ]] || die "No agent found in repo."

# ---- 3. Build the agent image --------------------------------------------
info "Building agent image (simploy-agent)..."
docker build -t "$AGENT_CONTAINER" -f "$WORK/src/Simploy.Agent/Dockerfile" "$WORK"

# ---- 4. Run the agent -----------------------------------------------------
info "Running agent container..."
mkdir -p "$INSTALL_DIR"
docker rm -f "$AGENT_CONTAINER" >/dev/null 2>&1 || true
# --network host lets the agent health-gate the apps it deploys (localhost:<port>),
# and expose :${AGENT_PORT} directly. Mounted docker.sock drives the builds.
docker run -d --name "$AGENT_CONTAINER" --restart unless-stopped \
  --network host \
  -v /var/run/docker.sock:/var/run/docker.sock \
  -v "${INSTALL_DIR}:/opt/simploy" \
  -e ASPNETCORE_HTTP_PORTS="${AGENT_PORT}" \
  -e Agent__Token="${AGENT_TOKEN}" \
  "$AGENT_CONTAINER"

# ---- 5. Verify ------------------------------------------------------------
sleep 2
info "Checking health at http://localhost:${AGENT_PORT}/health ..."
if curl -fsS "http://localhost:${AGENT_PORT}/health" >/dev/null 2>&1; then
  info "Agent is online on :${AGENT_PORT}."
  info "In Simploy UI add a Server: host=<this VM's IP>, and it will report Online."
else
  die "Agent did not come up - check: docker logs $AGENT_CONTAINER"
fi
