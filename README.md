# YouTube Downloader Bot

A Telegram bot that downloads YouTube videos and playlists as MP4 (video) or MP3 (audio).

## Features

- 🎬 **Video Download** - MP4 format with audio merged (using FFmpeg for 1080p+)
- 🎵 **Audio Download** - MP3 format converted via FFmpeg
- 📋 **Playlist Support** - Download entire playlists
- 🎯 **Quality Selection** - Choose from available video/audio qualities
- ⚡ **Auto FFmpeg Merge** - Automatically merges video + audio for high quality streams

## Requirements

- .NET 10.0 SDK
- FFmpeg (required for 1080p+ video and audio conversion)

## Setup

1. Create a Telegram bot via [@BotFather](https://t.me/BotFather)
2. Get your bot token
3. Set environment variable: `TELEGRAM_BOTTOKEN=your_token_here`
4. Run the bot

## Local Development

```bash
# Install FFmpeg is required
sudo apt install ffmpeg  # Ubuntu/Debian

dotnet run --project YouTubeDownloaderBot
```

## Docker Deployment

```bash
docker build -t youtube-bot .
docker run -e TELEGRAM_BOTTOKEN=your_token -p 5000:5000 youtube-bot
```

## VPS Deployment (All Bots)

Run the setup script to deploy both YouTube Downloader Bot and Cloudflare Worker Bot:

```bash
curl -fsSL https://raw.githubusercontent.com/NimaTalebzadeh/YouTubeDownloaderBot/main/deploy/setup-vps.sh | bash
```

This will:
- Install Docker & Docker Compose
- Clone both bot repositories
- Create shared docker-compose.yml
- Set up auto-update cron job (checks every minute)
- Start both bots

### Manual VPS Setup

```bash
# 1. Clone repos
git clone https://github.com/NimaTalebzadeh/YouTubeDownloaderBot.git /opt/bots/YouTubeDownloaderBot
git clone https://github.com/NimaTalebzadeh/CloudflareWorkerBot.git /opt/bots/CloudflareWorkerBot

# 2. Create .env with your tokens
cat > /opt/bots/.env << EOF
TELEGRAM_BOTTOKEN_YT=your_youtube_bot_token
TELEGRAM_BOTTOKEN_CF=your_cloudflare_bot_token
ADMIN_USER_IDS=your_telegram_id
EOF

# 3. Start with docker-compose
cd /opt/bots
docker compose up -d
```

## Commands

- `/start` - Start the bot and show menu
- `/help` - Show help message
- `/cancel` - Cancel current operation
- `/status` - Check session status
- `/checkffmpeg` - Verify FFmpeg installation

## How It Works

1. Send a YouTube video or playlist URL
2. Choose: **Video (MP4)** or **Audio (MP3)**
3. Select quality from available options
4. Bot downloads and sends the file

### Video Quality Options
- **Muxed streams** (≤720p): Video+audio combined, no FFmpeg needed
- **Adaptive streams** (1080p+): Separate video/audio, requires FFmpeg to merge

### Audio
- All audio converted to MP3 via FFmpeg

## File Limits

Telegram bots have a **50 MB file size limit**. Larger files will not be sendable.

## Auto-Update

The VPS setup includes a cron job that checks for git updates every minute and automatically rebuilds/restarts containers when changes are detected.

To manually trigger update:
```bash
/opt/bots/auto-update.sh
```

View logs:
```bash
docker logs -f youtube-downloader-bot
docker logs -f cloudflare-worker-bot
```