#!/usr/bin/env bash
# Deploy the LaadinfrastructuurPlanner Blazor app to the live Mac mini host.
# Live URL: https://route-analyse.nijenhuistrucksolutions.nl (via cloudflared tunnel)
# Host process: launchd label nl.nijenhuistrucksolutions.route-analyse on localhost:5198
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
APP_DIR="/Users/johnnynijenhuis/route-analyse/app"
STAGING="/tmp/route-analyse-deploy-$(date +%s)"
PLIST="$HOME/Library/LaunchAgents/nl.nijenhuistrucksolutions.route-analyse.plist"

export PATH="$HOME/.dotnet:$PATH"

echo "==> Publish Release build to $STAGING"
dotnet publish "$REPO_ROOT/src/LaadinfrastructuurPlanner" -c Release -o "$STAGING" >/dev/null

echo "==> Stop service"
launchctl unload "$PLIST" 2>/dev/null || true
sleep 2

echo "==> Rsync app files (preserving live.log and .cache)"
rsync -a --delete --exclude='live.log' --exclude='.cache' "$STAGING/" "$APP_DIR/"

echo "==> Start service"
launchctl load "$PLIST"

echo "==> Wait for health (max 60 s)"
for i in $(seq 1 30); do
  code=$(curl -s -o /dev/null -w '%{http_code}' http://localhost:5198/api/metadata || echo 000)
  if [ "$code" = "200" ]; then
    echo "==> Healthy after ${i} attempt(s)"
    rm -rf "$STAGING"
    echo "==> Done. Live at https://route-analyse.nijenhuistrucksolutions.nl"
    exit 0
  fi
  sleep 2
done

echo "!! Service did not return HTTP 200 within 60 s" >&2
echo "!! Check ~/Library/Logs/route-analyse.err.log" >&2
exit 1
