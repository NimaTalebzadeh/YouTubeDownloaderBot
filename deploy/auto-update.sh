#!/bin/bash
set -e

cd /opt/bots

REPOS=("AdvancedCalculaterBot" "CloudflareWorkerBot" "YouTubeDownloaderBot")
BOTS=("calculator-bot" "cloudflare-bot" "youtube-bot")

for i in "${!REPOS[@]}"; do
  repo="${REPOS[$i]}"
  bot="${BOTS[$i]}"

  if [ ! -d "$repo/.git" ]; then
    echo "[$(date)] Skipping $repo - no git repo found"
    continue
  fi

  cd "$repo"
  git fetch origin
  local=$(git rev-parse HEAD)
  remote=$(git rev-parse @{u})

  if [ "$local" != "$remote" ]; then
    echo "[$(date)] Updates found for $repo. Pulling and rebuilding..."
    git pull
    cd /opt/bots
    docker compose build --no-cache "$bot"
    docker compose up -d "$bot"
  else
    cd /opt/bots
  fi
done

echo "[$(date)] Auto-update check complete"