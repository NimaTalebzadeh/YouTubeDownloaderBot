using System.Text;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using YouTubeDownloaderBot.Services;
using YouTubeDownloaderBot.State;

namespace YouTubeDownloaderBot.Telegram;

public sealed class BotUpdateRouter
{
    private readonly UserSessionManager _sessionManager;
    private readonly ConversationHandler _conversationHandler;
    private readonly YouTubeDownloadService _downloadService;
    private readonly ILogger<BotUpdateRouter> _logger;

    public BotUpdateRouter(
        UserSessionManager sessionManager,
        ConversationHandler conversationHandler,
        YouTubeDownloadService downloadService,
        ILogger<BotUpdateRouter> logger)
    {
        _sessionManager = sessionManager;
        _conversationHandler = conversationHandler;
        _downloadService = downloadService;
        _logger = logger;
    }

    public async Task HandleAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
    {
        try
        {
            if (update.CallbackQuery is { } callback)
            {
                await HandleCallbackQueryAsync(bot, callback, ct);
                return;
            }

            var message = update.Message;
            if (message is null) return;

            var chatId = message.Chat.Id;
            var userId = message.From?.Id ?? 0;

            if (message.Text is null) return;

            var text = message.Text.Trim();

            if (text.StartsWith("/"))
            {
                var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var command = parts[0].ToLowerInvariant();

                switch (command)
                {
                    case "/start":
                        await HandleStartAsync(bot, chatId, userId, ct);
                        return;

                    case "/help":
                        await HandleHelpAsync(bot, chatId, ct);
                        return;

                    case "/cancel":
                    case "/reset":
                        _sessionManager.ResetSession(userId);
                        await bot.SendMessage(chatId,
                            "<b>Session cancelled.</b>\n\nSend /start to begin again.",
                            parseMode: ParseMode.Html, cancellationToken: ct);
                        return;

                    case "/status":
                        await HandleStatusAsync(bot, chatId, userId, ct);
                        return;

                    case "/checkffmpeg":
                        await HandleCheckFfmpegAsync(bot, chatId, ct);
                        return;

                    default:
                        await bot.SendMessage(chatId,
                            "<b>Unknown command.</b>\n\nSend /help to see available commands.",
                            parseMode: ParseMode.Html, cancellationToken: ct);
                        return;
                }
            }

            // Handle text input in conversation flow
            if (_sessionManager.TryGetSession(userId, out var session) &&
                session.CurrentStep is not DownloadStep.Start and not DownloadStep.Done and not DownloadStep.Error and not DownloadStep.Downloading)
            {
                await _conversationHandler.HandleAsync(bot, chatId, userId, text, ct);
            }
            else
            {
                var keyboard = new InlineKeyboardMarkup(new[]
                {
                    new[] { InlineKeyboardButton.WithCallbackData("📥 Download Video", "type:video"), InlineKeyboardButton.WithCallbackData("🎵 Download Audio", "type:audio") }
                });
                
                await bot.SendMessage(chatId,
                    "Send me a YouTube video or playlist URL to get started.\n\n" +
                    "Or use /help for more commands.",
                    replyMarkup: keyboard, cancellationToken: ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling update {UpdateId}", update.Id);
        }
    }

    private async Task HandleStartAsync(ITelegramBotClient bot, long chatId, long userId, CancellationToken ct)
    {
        _sessionManager.ResetSession(userId);
        var session = _sessionManager.GetOrCreateSession(userId);
        session.CurrentStep = DownloadStep.WaitingForUrl;

        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("📥 Download Video", "type:video"), InlineKeyboardButton.WithCallbackData("🎵 Download Audio", "type:audio") },
            new[] { InlineKeyboardButton.WithCallbackData("ℹ️ Help", "cmd:help"), InlineKeyboardButton.WithCallbackData("🔧 Check FFmpeg", "cmd:checkffmpeg") }
        });

        await bot.SendMessage(chatId,
            "<b>🎬 YouTube Downloader Bot</b>\n\n" +
            "Send me a <b>YouTube video URL</b> or <b>playlist URL</b> and I'll help you download it.\n\n" +
            "<b>Features:</b>\n" +
            "• Download videos in MP4 format (with audio merged)\n" +
            "• Download audio only in MP3 format\n" +
            "• Support for playlists\n" +
            "• Multiple quality options\n" +
            "• Automatic FFmpeg merging for high quality\n\n" +
            "<b>Just send a YouTube link to begin!</b>",
            parseMode: ParseMode.Html,
            replyMarkup: keyboard,
            cancellationToken: ct);
    }

    private async Task HandleHelpAsync(ITelegramBotClient bot, long chatId, CancellationToken ct)
    {
        const string helpMessage = """
            <b>YouTube Downloader Bot - Help</b>

            <b>Commands:</b>
              /start        - Start the bot and show main menu
              /help         - Show this help message
              /cancel       - Cancel current operation
              /status       - Check current session status
              /checkffmpeg  - Verify FFmpeg installation

            <b>How to use:</b>
            1. Send a YouTube video or playlist URL
            2. Choose: Video (MP4) or Audio (MP3)
            3. Select quality from available options
            4. Wait for download and receive the file

            <b>Quality Options:</b>
            • <b>Muxed streams</b> (≤720p) - Video+audio combined, no FFmpeg needed
            • <b>Adaptive streams</b> (1080p+) - Separate video/audio, FFmpeg required

            <b>Format:</b>
            • Videos: Always MP4
            • Audio: Always MP3 (converted via FFmpeg)

            <b>Limits:</b>
            • Telegram bot file limit: 50 MB
            • Large files may not be sendable

            <b>Note:</b> FFmpeg must be installed on the server for high-quality downloads and audio conversion.
            """;

        await bot.SendMessage(chatId, helpMessage, parseMode: ParseMode.Html, cancellationToken: ct);
    }

    private async Task HandleStatusAsync(ITelegramBotClient bot, long chatId, long userId, CancellationToken ct)
    {
        if (!_sessionManager.TryGetSession(userId, out var session))
        {
            await bot.SendMessage(chatId,
                "<b>No active session.</b>\n\nSend /start to begin.",
                parseMode: ParseMode.Html, cancellationToken: ct);
            return;
        }

        var stepText = session.CurrentStep switch
        {
            DownloadStep.WaitingForUrl => "Waiting for YouTube URL",
            DownloadStep.WaitingForType => "Waiting for type selection (Video/Audio)",
            DownloadStep.WaitingForVideoQuality => "Waiting for video quality selection",
            DownloadStep.WaitingForAudioQuality => "Waiting for audio quality selection",
            DownloadStep.Downloading => "Downloading...",
            DownloadStep.Done => "Completed",
            DownloadStep.Error => "Error occurred",
            _ => "Ready"
        };

        var urlInfo = session.Url != null ? $"\n<b>URL:</b> <code>{EscapeHtml(session.Url)}</code>" : "";
        var titleInfo = session.VideoTitle != null ? $"\n<b>Title:</b> {EscapeHtml(session.VideoTitle)}" : "";
        var typeInfo = session.DownloadType != DownloadType.None ? $"\n<b>Type:</b> {session.DownloadType}" : "";

        await bot.SendMessage(chatId,
            $"<b>Session Status</b>\n\n" +
            $"Step: <b>{stepText}</b>" +
            $"{urlInfo}" +
            $"{titleInfo}" +
            $"{typeInfo}\n\n" +
            $"Last activity: {session.LastActivity:HH:mm:ss} UTC",
            parseMode: ParseMode.Html, cancellationToken: ct);
    }

    private async Task HandleCheckFfmpegAsync(ITelegramBotClient bot, long chatId, CancellationToken ct)
    {
        await bot.SendMessage(chatId, "🔧 Checking FFmpeg installation...", cancellationToken: ct);

        var hasFfmpeg = await _downloadService.CheckDependenciesAsync();

        if (hasFfmpeg)
        {
            // Get version
            string version = "Unknown";
            try
            {
                var process = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "ffmpeg",
                        Arguments = "-version",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                process.Start();
                var output = await process.StandardOutput.ReadLineAsync();
                await process.WaitForExitAsync();
                version = output ?? "Unknown";
            }
            catch { }

            await bot.SendMessage(chatId,
                $"✅ <b>FFmpeg is installed and working!</b>\n\n" +
                $"<code>{EscapeHtml(version)}</code>\n\n" +
                "High-quality video downloads (1080p+) and audio conversion to MP3 are available.",
                parseMode: ParseMode.Html, cancellationToken: ct);
        }
        else
        {
            await bot.SendMessage(chatId,
                "❌ <b>FFmpeg NOT FOUND</b>\n\n" +
                "FFmpeg is required for:\n" +
                "• Downloading 1080p, 1440p, 4K videos\n" +
                "• Converting audio to MP3\n" +
                "• Merging video + audio streams\n\n" +
                "<b>Install FFmpeg:</b>\n" +
                "• Ubuntu/Debian: <code>sudo apt update && sudo apt install ffmpeg</code>\n" +
                "• Alpine: <code>apk add ffmpeg</code>\n" +
                "• Docker: Use the provided Dockerfile which includes FFmpeg\n\n" +
                "Without FFmpeg, only muxed streams (≤720p) can be downloaded.",
                parseMode: ParseMode.Html, cancellationToken: ct);
        }
    }

    private async Task HandleCallbackQueryAsync(ITelegramBotClient bot, CallbackQuery callback, CancellationToken ct)
    {
        var chatId = callback.Message?.Chat.Id ?? 0;
        var userId = callback.From.Id;
        var data = callback.Data ?? "";

        await bot.AnswerCallbackQuery(callback.Id, cancellationToken: ct);

        if (data.StartsWith("type:") || data.StartsWith("quality:") || data.StartsWith("audioq:") || data.StartsWith("confirm:"))
        {
            await _conversationHandler.HandleCallbackAsync(bot, chatId, userId, data, ct);
        }
        else if (data.StartsWith("cmd:"))
        {
            var cmd = data[4..];
            switch (cmd)
            {
                case "help":
                    await HandleHelpAsync(bot, chatId, ct);
                    break;
                case "checkffmpeg":
                    await HandleCheckFfmpegAsync(bot, chatId, ct);
                    break;
            }
        }
    }

    private static string EscapeHtml(string text) =>
        text.Replace("&", "&").Replace("<", "<").Replace(">", ">");
}