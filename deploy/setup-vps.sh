#!/bin/bash
set -e

echo "=== VPS Setup for All Telegram Bots ==="

# Install Docker
if ! command -v docker &> /dev/null; then
    echo "Installing Docker..."
    curl -fsSL https://get.docker.com | sh
    systemctl enable docker
    systemctl start docker
fi

# Install Docker Compose plugin
if ! docker compose version &> /dev/null; then
    echo "Installing Docker Compose..."
    apt-get update && apt-get install -y docker-compose-plugin
fi

BOTS_DIR="/opt/bots"
mkdir -p "$BOTS_DIR"

# Clone bot repositories
echo "Cloning AdvancedCalculaterBot..."
git clone https://github.com/NimaTalebzadeh/AdvancedCalculaterBot.git "$BOTS_DIR/AdvancedCalculaterBot" 2>/dev/null || echo "Repo already exists"

echo "Cloning CloudflareWorkerBot..."
git clone https://github.com/NimaTalebzadeh/CloudflareWorkerBot.git "$BOTS_DIR/CloudflareWorkerBot" 2>/dev/null || echo "Repo already exists"

echo "Cloning YouTubeDownloaderBot..."
git clone https://github.com/NimaTalebzadeh/YouTubeDownloaderBot.git "$BOTS_DIR/YouTubeDownloaderBot" 2>/dev/null || echo "Repo already exists"

# Create .env file if not exists
if [ ! -f "$BOTS_DIR/.env" ]; then
    echo "Creating .env file..."
    cat > "$BOTS_DIR/.env" << 'EOF'
# Advanced Calculator Bot
TELEGRAM_BOTTOKEN_CALC=your_calculator_bot_token_here
ADMIN_IDS_CALC=your_telegram_user_id_here

# Cloudflare Worker Bot
TELEGRAM_BOTTOKEN_CF=your_cloudflare_bot_token_here
ADMIN_USER_IDS=your_telegram_user_id_here

# YouTube Downloader Bot
TELEGRAM_BOTTOKEN_YT=your_youtube_bot_token_here
EOF
    echo ""
    echo "!!! EDIT $BOTS_DIR/.env with your real bot tokens !!!"
    echo ""
fi

# Create shared docker-compose.yml
cat > "$BOTS_DIR/docker-compose.yml" << 'DOCKERCOMPOSE'
services:
  calculator-bot:
    build: ./AdvancedCalculaterBot
    container_name: advanced-calculator-bot
    restart: unless-stopped
    environment:
      - TELEGRAM_BOTTOKEN=${TELEGRAM_BOTTOKEN_CALC}
      - ADMIN_IDS=${ADMIN_IDS_CALC}
      - PORT=5001
    ports:
      - "5001:5001"

  cloudflare-bot:
    build: ./CloudflareWorkerBot
    container_name: cloudflare-worker-bot
    restart: unless-stopped
    environment:
      - TELEGRAM_BOTTOKEN=${TELEGRAM_BOTTOKEN_CF}
      - ADMIN_USER_IDS=${ADMIN_USER_IDS}
      - PORT=5002
    ports:
      - "5002:5002"

  youtube-bot:
    build: ./YouTubeDownloaderBot
    container_name: youtube-downloader-bot
    restart: unless-stopped
    environment:
      - TELEGRAM_BOTTOKEN=${TELEGRAM_BOTTOKEN_YT}
      - PORT=5000
    ports:
      - "5000:5000"
DOCKERCOMPOSE

# Create auto-update script
cat > "$BOTS_DIR/auto-update.sh" << 'AUTOUPDATE'
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
AUTOUPDATE

chmod +x "$BOTS_DIR/auto-update.sh"

# Start all bots
echo "Starting all bots..."
cd "$BOTS_DIR"
docker compose up -d

# Set up cron job for auto-update (every minute)
echo "Setting up auto-update cron job (every minute)..."
(crontab -l 2>/dev/null | grep -v auto-update; echo "* * * * * $BOTS_DIR/auto-update.sh >> /var/log/bots-update.log 2>&1") | crontab -

echo ""
echo "=== Setup Complete ==="
echo "Bots directory: $BOTS_DIR"
echo "Auto-update checks every minute via cron"
echo "Update logs: /var/log/bots-update.log"
echo ""
echo "To view bot logs:"
echo "  docker logs -f advanced-calculator-bot"
echo "  docker logs -f cloudflare-worker-bot"
echo "  docker logs -f youtube-downloader-bot"
echo ""
echo "To manually trigger update:"
echo "  /opt/bots/auto-update.sh"
echo ""
echo "!!! IMPORTANT: Edit $BOTS_DIR/.env with your bot tokens before the bots will work !!!"