using System.Text;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using YouTubeDownloaderBot.Services;
using YouTubeDownloaderBot.State;

namespace YouTubeDownloaderBot.Telegram;

public sealed class ConversationHandler
{
    private readonly UserSessionManager _sessionManager;
    private readonly YouTubeDownloadService _downloadService;
    private readonly InstagramDownloadService _instagramService;
    private readonly ILogger<ConversationHandler> _logger;

    public ConversationHandler(
        UserSessionManager sessionManager,
        YouTubeDownloadService downloadService,
        InstagramDownloadService instagramService,
        ILogger<ConversationHandler> logger)
    {
        _sessionManager = sessionManager;
        _downloadService = downloadService;
        _instagramService = instagramService;
        _logger = logger;
    }

    public async Task HandleAsync(ITelegramBotClient bot, long chatId, long userId, string text, CancellationToken ct)
    {
        var session = _sessionManager.GetOrCreateSession(userId);
        session.LastActivity = DateTime.UtcNow;

        if (session.CurrentStep is DownloadStep.Downloading or DownloadStep.Error)
            return;

        try
        {
            switch (session.CurrentStep)
            {
                case DownloadStep.WaitingForUrl:
                    await HandleUrlStep(bot, chatId, session, text, ct);
                    break;
                case DownloadStep.WaitingForType:
                    await HandleTypeStep(bot, chatId, session, text, ct);
                    break;
                case DownloadStep.WaitingForVideoQuality:
                    await HandleVideoQualityStep(bot, chatId, session, text, ct);
                    break;
                case DownloadStep.WaitingForAudioQuality:
                    await HandleAudioQualityStep(bot, chatId, session, text, ct);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in conversation for user {UserId}", userId);
            session.CurrentStep = DownloadStep.Error;
            _sessionManager.UpdateSession(session);
            await bot.SendMessage(chatId,
                $"<b>An error occurred:</b>\n<code>{EscapeHtml(ex.Message)}</code>\n\nSend /start to try again.",
                parseMode: ParseMode.Html, cancellationToken: ct);
        }
    }

    private async Task HandleUrlStep(ITelegramBotClient bot, long chatId, UserDownloadSession session, string input, CancellationToken ct)
    {
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri))
        {
            await bot.SendMessage(chatId, "Please send a valid URL (starts with https://)", cancellationToken: ct);
            return;
        }

        var isInstagram = IsInstagramUrl(uri);
        if (!IsYouTubeUrl(uri) && !isInstagram)
        {
            await bot.SendMessage(chatId, "Please send a valid YouTube or Instagram URL.", cancellationToken: ct);
            return;
        }

        session.Platform = isInstagram ? SourcePlatform.Instagram : SourcePlatform.YouTube;

        var isPlaylist = IsPlaylistUrl(uri);
        session.IsPlaylist = isPlaylist;

        // For playlists, use the first video URL to get available stream qualities
        var urlForInfo = input;
        if (isPlaylist)
        {
            session.Url = input;
            try
            {
                var playlistInfo = await _downloadService.GetPlaylistInfoAsync(input, ct);
                if (playlistInfo.VideoUrls.Count == 0)
                {
                    await bot.SendMessage(chatId, "This playlist is empty. Please try another URL.", cancellationToken: ct);
                    return;
                }
                urlForInfo = playlistInfo.VideoUrls[0];
                session.VideoTitle = playlistInfo.Title;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get playlist info for URL: {Url}", input);
                await bot.SendMessage(chatId, $"Could not fetch playlist info: {ex.Message}\n\nSend /start to try again.", cancellationToken: ct);
                session.CurrentStep = DownloadStep.Error;
                _sessionManager.UpdateSession(session);
                return;
            }
        }
        else
        {
            session.Url = input;
        }

        await bot.SendMessage(chatId, "Fetching video info...", cancellationToken: ct);

        try
        {
            VideoInfo info;
            if (session.Platform == SourcePlatform.Instagram)
            {
                info = await _instagramService.GetInfoAsync(urlForInfo, ct);
            }
            else
            {
                info = await _downloadService.GetVideoInfoAsync(urlForInfo, ct);
            }

            session.VideoTitle = info.Title;
            session.VideoOptions = info.VideoOptions;
            session.AudioOptions = info.AudioOptions;

            var typeKeyboard = new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("📹 Video", "type:video"),
                    InlineKeyboardButton.WithCallbackData("🎵 Audio Only", "type:audio")
                }
            });

            var infoMsg = isPlaylist
                ? $"<b>Playlist:</b> {EscapeHtml(session.VideoTitle ?? info.Title)}\n<b>Videos:</b> {info.VideoOptions.Count} quality options\n\n"
                : $"<b>Video:</b> {EscapeHtml(info.Title)}\n<b>Channel:</b> {EscapeHtml(info.Author)}\n<b>Duration:</b> {info.Duration?.ToString(@"hh\:mm\:ss") ?? "Unknown"}\n\n";

            await bot.SendMessage(chatId, infoMsg + "What would you like to download?",
                parseMode: ParseMode.Html,
                replyMarkup: typeKeyboard,
                cancellationToken: ct);

            session.CurrentStep = DownloadStep.WaitingForType;
            _sessionManager.UpdateSession(session);
        }
        catch (ArgumentException)
        {
            await bot.SendMessage(chatId, "Invalid URL. Please make sure it's a valid YouTube video/playlist or Instagram link.", cancellationToken: ct);
        }
        catch (HttpRequestException ex)
        {
            await bot.SendMessage(chatId, $"Network error: {ex.Message}\n\nTry again later.", cancellationToken: ct);
        }
        catch (Exception ex) when (ex.Message.Contains("yt-dlp", StringComparison.OrdinalIgnoreCase))
        {
            await bot.SendMessage(chatId,
                "⚠️ <b>yt-dlp not found</b>\n\n" +
                "Instagram downloads require yt-dlp to be installed on the server.\n" +
                "Install: <code>pip install yt-dlp</code> or <code>brew install yt-dlp</code>",
                parseMode: ParseMode.Html,
                cancellationToken: ct);
            session.CurrentStep = DownloadStep.Error;
            _sessionManager.UpdateSession(session);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get video info");
            await bot.SendMessage(chatId, $"Error fetching video info: {ex.Message}\n\nSend /start to try again.", cancellationToken: ct);
            session.CurrentStep = DownloadStep.Error;
            _sessionManager.UpdateSession(session);
        }
    }

    private async Task HandleTypeStep(ITelegramBotClient bot, long chatId, UserDownloadSession session, string input, CancellationToken ct)
    {
        if (input == "video")
        {
            session.DownloadType = DownloadType.Video;
            
            if (session.VideoOptions == null || session.VideoOptions.Count == 0)
            {
                await bot.SendMessage(chatId, "No video qualities available for this video.", cancellationToken: ct);
                session.CurrentStep = DownloadStep.Error;
                _sessionManager.UpdateSession(session);
                return;
            }

            var keyboard = BuildVideoQualityKeyboard(session.VideoOptions);
            
            await bot.SendMessage(chatId,
                "<b>Select video quality:</b>",
                parseMode: ParseMode.Html,
                replyMarkup: keyboard,
                cancellationToken: ct);

            session.CurrentStep = DownloadStep.WaitingForVideoQuality;
            _sessionManager.UpdateSession(session);
        }
        else if (input == "audio")
        {
            session.DownloadType = DownloadType.Audio;
            
            if (session.AudioOptions == null || session.AudioOptions.Count == 0)
            {
                await bot.SendMessage(chatId, "No audio qualities available for this video.", cancellationToken: ct);
                session.CurrentStep = DownloadStep.Error;
                _sessionManager.UpdateSession(session);
                return;
            }

            var keyboard = BuildAudioQualityKeyboard(session.AudioOptions);
            
            await bot.SendMessage(chatId,
                "<b>Select audio quality:</b>\n\n" +
                "All audio will be converted to <b>MP3</b> format using FFmpeg.",
                parseMode: ParseMode.Html,
                replyMarkup: keyboard,
                cancellationToken: ct);

            session.CurrentStep = DownloadStep.WaitingForAudioQuality;
            _sessionManager.UpdateSession(session);
        }
        else
        {
            await bot.SendMessage(chatId, "Please select Video or Audio using the buttons.", cancellationToken: ct);
        }
    }

    private async Task HandleVideoQualityStep(ITelegramBotClient bot, long chatId, UserDownloadSession session, string input, CancellationToken ct)
    {
        if (!int.TryParse(input, out int index) || index < 0 || index >= (session.VideoOptions?.Count ?? 0))
        {
            await bot.SendMessage(chatId, "Invalid selection. Please use the buttons.", cancellationToken: ct);
            return;
        }

        session.SelectedQualityIndex = index;
        var option = session.VideoOptions![index];

        await StartVideoDownload(bot, chatId, session, ct);
    }

    private async Task HandleAudioQualityStep(ITelegramBotClient bot, long chatId, UserDownloadSession session, string input, CancellationToken ct)
    {
        if (!int.TryParse(input, out int index) || index < 0 || index >= (session.AudioOptions?.Count ?? 0))
        {
            await bot.SendMessage(chatId, "Invalid selection. Please use the buttons.", cancellationToken: ct);
            return;
        }

        session.SelectedQualityIndex = index;
        await StartAudioDownload(bot, chatId, session, ct);
    }

    public async Task HandleCallbackAsync(ITelegramBotClient bot, long chatId, long userId, string data, CancellationToken ct)
    {
        var session = _sessionManager.GetOrCreateSession(userId);

        if (data.StartsWith("type:"))
        {
            if (session.CurrentStep != DownloadStep.WaitingForType)
            {
                await bot.SendMessage(chatId, "Please send a YouTube or Instagram URL first, then select the type.", cancellationToken: ct);
                return;
            }

            var type = data[5..];
            await HandleTypeStep(bot, chatId, session, type, ct);
        }
        else if (data.StartsWith("quality:"))
        {
            if (session.CurrentStep != DownloadStep.WaitingForVideoQuality)
            {
                await bot.SendMessage(chatId, "Please select the download type and quality in order.", cancellationToken: ct);
                return;
            }

            var indexStr = data[8..];
            await HandleVideoQualityStep(bot, chatId, session, indexStr, ct);
        }
        else if (data.StartsWith("audioq:"))
        {
            if (session.CurrentStep != DownloadStep.WaitingForAudioQuality)
            {
                await bot.SendMessage(chatId, "Please select the download type and quality in order.", cancellationToken: ct);
                return;
            }

            var indexStr = data[7..];
            await HandleAudioQualityStep(bot, chatId, session, indexStr, ct);
        }
        else if (data.StartsWith("confirm:"))
        {
            if (session.CurrentStep != DownloadStep.Downloading)
            {
                await bot.SendMessage(chatId, "No pending download to confirm. Send /start to begin.", cancellationToken: ct);
                return;
            }

            var confirm = data[8..];
            if (confirm == "yes")
            {
                if (session.DownloadType == DownloadType.Video)
                    await StartVideoDownload(bot, chatId, session, ct);
                else
                    await StartAudioDownload(bot, chatId, session, ct);
            }
            else
            {
                await bot.SendMessage(chatId, "Download cancelled. Send /start to begin again.", cancellationToken: ct);
                session.CurrentStep = DownloadStep.Start;
                _sessionManager.UpdateSession(session);
            }
        }
    }

    private async Task StartVideoDownload(ITelegramBotClient bot, long chatId, UserDownloadSession session, CancellationToken ct)
    {
        if (session.Url == null || session.VideoOptions == null || session.SelectedQualityIndex < 0)
            return;

        session.CurrentStep = DownloadStep.Downloading;
        _sessionManager.UpdateSession(session);

        var option = session.VideoOptions[session.SelectedQualityIndex];
        var title = session.VideoTitle ?? "video";

        if (session.Platform == SourcePlatform.Instagram)
        {
            await HandleInstagramDownload(bot, chatId, session, false, title, ct);
        }
        else if (session.IsPlaylist && session.VideoOptions.Count > 0)
        {
            await HandlePlaylistVideoDownload(bot, chatId, session, option, title, ct);
        }
        else
        {
            await HandleSingleVideoDownload(bot, chatId, session, option, title, ct);
        }
    }

    private async Task HandleSingleVideoDownload(
        ITelegramBotClient bot, 
        long chatId, 
        UserDownloadSession session, 
        VideoQualityOption option, 
        string title, 
        CancellationToken ct)
    {
        var progressMessage = await bot.SendMessage(chatId, "📥 Downloading... 0%", cancellationToken: ct);
        var progress = new Progress<double>(p => 
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await bot.EditMessageText(chatId, progressMessage.MessageId, $"📥 Downloading... {p:P0}");
                }
                catch { }
            });
        });

        try
        {
            var result = await _downloadService.DownloadVideoAsync(
                session.Url!, option, title, progress, ct);

            if (result.FromCache)
            {
                await bot.EditMessageText(chatId, progressMessage.MessageId, "⚡ <b>Found in cache!</b> Sending instantly...", parseMode: ParseMode.Html, cancellationToken: ct);
            }

            var filePath = result.FilePath;
            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Length > 50 * 1024 * 1024)
            {
                await bot.EditMessageText(chatId, progressMessage.MessageId, "⚠️ File > 50MB, splitting into parts...", cancellationToken: ct);
                var parts = await _downloadService.SplitVideoAsync(filePath, ct);
                
                foreach (var part in parts)
                {
                    await using var partStream = File.OpenRead(part);
                    await bot.SendVideo(
                        chatId,
                        InputFile.FromStream(partStream, Path.GetFileName(part)),
                        caption: $"✅ {EscapeHtml(title)} (Part {parts.IndexOf(part) + 1}/{parts.Count})",
                        supportsStreaming: true,
                        cancellationToken: ct);
                }
                
                // Clean up split parts
                foreach (var part in parts)
                {
                    try { File.Delete(part); } catch { }
                }
            }
            else
            {
                await using var fileStream = File.OpenRead(filePath);
                await bot.DeleteMessage(chatId, progressMessage.MessageId, ct);
                await bot.SendVideo(
                    chatId,
                    InputFile.FromStream(fileStream, Path.GetFileName(filePath)),
                    caption: $"✅ {EscapeHtml(title)}",
                    supportsStreaming: true,
                    cancellationToken: ct);
            }

            session.CurrentStep = DownloadStep.WaitingForUrl;
            _sessionManager.UpdateSession(session);

            await bot.SendMessage(chatId,
                $"✅ <b>Download complete!</b>\n\nSend another YouTube or Instagram link to continue, or send /cancel.\n\n<b>{EscapeHtml(title)}</b> has been sent.",
                parseMode: ParseMode.Html, cancellationToken: ct);

            // Clean up the temp working file only — never delete a cached copy.
            if (!result.FromCache)
            {
                try { File.Delete(filePath); } catch { }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Video download failed");
            await bot.EditMessageText(chatId, progressMessage.MessageId, 
                $"❌ <b>Download failed:</b>\n<code>{EscapeHtml(ex.Message)}</code>\n\nSend /start to try again.",
                parseMode: ParseMode.Html, cancellationToken: ct);
            session.CurrentStep = DownloadStep.Error;
            _sessionManager.UpdateSession(session);
        }
    }

    private async Task HandlePlaylistVideoDownload(
        ITelegramBotClient bot, 
        long chatId, 
        UserDownloadSession session, 
        VideoQualityOption option, 
        string playlistTitle, 
        CancellationToken ct)
    {
        var playlistInfo = await _downloadService.GetPlaylistInfoAsync(session.Url!, ct);
        var urls = playlistInfo.VideoUrls;
        
        var progressMessage = await bot.SendMessage(chatId, $"📥 Starting playlist download (0/{urls.Count})...", cancellationToken: ct);
        
        var progress = new Progress<(int current, int total, string title)>((p) => 
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await bot.EditMessageText(chatId, progressMessage.MessageId,
                        $"📥 Downloading playlist: [{p.current}/{p.total}]\n{p.title}");
                }
                catch { }
            });
        });

        try
        {
            var playlistDir = await _downloadService.DownloadPlaylistVideosAsync(
                urls, option, playlistTitle, progress, ct);

            await bot.EditMessageText(chatId, progressMessage.MessageId, 
                "📦 Preparing files for upload...", cancellationToken: ct);

            // Send files one by one
            var files = Directory.GetFiles(playlistDir, "*.mp4").OrderBy(f => f).ToList();
            int sent = 0;

            foreach (var filePath in files)
            {
                try
                {
                    var fileInfo = new FileInfo(filePath);
                    if (fileInfo.Length > 50 * 1024 * 1024)
                    {
                        var parts = await _downloadService.SplitVideoAsync(filePath, ct);

                        foreach (var part in parts)
                        {
                            await using var partStream = File.OpenRead(part);
                            await bot.SendVideo(
                                chatId,
                                InputFile.FromStream(partStream, Path.GetFileName(part)),
                                caption: $"✅ {EscapeHtml(playlistTitle)} (Part {parts.IndexOf(part) + 1}/{parts.Count})",
                                supportsStreaming: true,
                                cancellationToken: ct);
                        }

                        // Clean up split parts
                        foreach (var part in parts)
                        {
                            try { File.Delete(part); } catch { }
                        }
                    }
                    else
                    {
                        await using var fileStream = File.OpenRead(filePath);
                        await bot.SendVideo(
                            chatId,
                            InputFile.FromStream(fileStream, Path.GetFileName(filePath)),
                            caption: $"✅ {EscapeHtml(playlistTitle)}",
                            supportsStreaming: true,
                            cancellationToken: ct);
                    
                        sent++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to send file: {File}", filePath);
                }
            }

            await bot.SendMessage(chatId,
                $"✅ <b>Playlist download complete!</b>\n\n" +
                $"Sent: {sent}/{files.Count} videos\n" +
                $"Failed: {files.Count - sent}\n\nSend another link to continue, or send /cancel.",
                parseMode: ParseMode.Html, cancellationToken: ct);

            session.CurrentStep = DownloadStep.WaitingForUrl;
            _sessionManager.UpdateSession(session);

            // Cleanup
            try { Directory.Delete(playlistDir, true); } catch { }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Playlist download failed");
            await bot.EditMessageText(chatId, progressMessage.MessageId, 
                $"❌ <b>Playlist download failed:</b>\n<code>{EscapeHtml(ex.Message)}</code>\n\nSend /start to try again.",
                parseMode: ParseMode.Html, cancellationToken: ct);
            session.CurrentStep = DownloadStep.Error;
            _sessionManager.UpdateSession(session);
        }
    }

    private async Task HandleInstagramDownload(
        ITelegramBotClient bot, 
        long chatId, 
        UserDownloadSession session, 
        bool isAudio, 
        string title, 
        CancellationToken ct)
    {
        var emoji = isAudio ? "🎵" : "📹";
        var progressMessage = await bot.SendMessage(chatId, $"{emoji} Downloading from Instagram...", cancellationToken: ct);

        try
        {
            var result = await _instagramService.DownloadAsync(session.Url!, title, isAudio, ct);

            var filePath = result.FilePath;
            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Length > 50 * 1024 * 1024)
            {
                await bot.EditMessageText(chatId, progressMessage.MessageId, "⚠️ File > 50MB, splitting into parts...", cancellationToken: ct);
                var parts = await _downloadService.SplitVideoAsync(filePath, ct);

                foreach (var part in parts)
                {
                    await using var partStream = File.OpenRead(part);
                    if (isAudio)
                    {
                        await bot.SendAudio(
                            chatId,
                            InputFile.FromStream(partStream, Path.GetFileName(part)),
                            caption: $"✅ {EscapeHtml(title)} (Part {parts.IndexOf(part) + 1}/{parts.Count})",
                            cancellationToken: ct);
                    }
                    else
                    {
                        await bot.SendVideo(
                            chatId,
                            InputFile.FromStream(partStream, Path.GetFileName(part)),
                            caption: $"✅ {EscapeHtml(title)} (Part {parts.IndexOf(part) + 1}/{parts.Count})",
                            supportsStreaming: true,
                            cancellationToken: ct);
                    }
                }

                // Clean up split parts
                foreach (var part in parts)
                {
                    try { File.Delete(part); } catch { }
                }
            }
            else
            {
                await using var fileStream = File.OpenRead(filePath);
                await bot.DeleteMessage(chatId, progressMessage.MessageId, ct);
                if (isAudio)
                {
                    await bot.SendAudio(
                        chatId,
                        InputFile.FromStream(fileStream, Path.GetFileName(filePath)),
                        caption: $"✅ {EscapeHtml(title)}",
                        cancellationToken: ct);
                }
                else
                {
                    await bot.SendVideo(
                        chatId,
                        InputFile.FromStream(fileStream, Path.GetFileName(filePath)),
                        caption: $"✅ {EscapeHtml(title)}",
                        supportsStreaming: true,
                        cancellationToken: ct);
                }
            }

            session.CurrentStep = DownloadStep.WaitingForUrl;
            _sessionManager.UpdateSession(session);

            await bot.SendMessage(chatId,
                $"✅ <b>Download complete!</b>\n\nSend another YouTube or Instagram link to continue, or send /cancel.\n\n<b>{EscapeHtml(title)}</b> has been sent.",
                parseMode: ParseMode.Html, cancellationToken: ct);

            // Clean up the temp working file only — never delete a cached copy.
            if (!result.FromCache)
            {
                try { File.Delete(filePath); } catch { }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Instagram download failed");
            await bot.EditMessageText(chatId, progressMessage.MessageId, 
                $"❌ <b>Download failed:</b>\n<code>{EscapeHtml(ex.Message)}</code>\n\nSend /start to try again.",
                parseMode: ParseMode.Html, cancellationToken: ct);
            session.CurrentStep = DownloadStep.Error;
            _sessionManager.UpdateSession(session);
        }
    }

    private async Task StartAudioDownload(ITelegramBotClient bot, long chatId, UserDownloadSession session, CancellationToken ct)
    {
        if (session.Url == null || session.AudioOptions == null || session.SelectedQualityIndex < 0)
            return;

        session.CurrentStep = DownloadStep.Downloading;
        _sessionManager.UpdateSession(session);

        var option = session.AudioOptions[session.SelectedQualityIndex];
        var title = session.VideoTitle ?? "audio";

        if (session.Platform == SourcePlatform.Instagram)
        {
            await HandleInstagramDownload(bot, chatId, session, true, title, ct);
        }
        else if (session.IsPlaylist && session.AudioOptions.Count > 0)
        {
            await HandlePlaylistAudioDownload(bot, chatId, session, option, title, ct);
        }
        else
        {
            await HandleSingleAudioDownload(bot, chatId, session, option, title, ct);
        }
    }

    private async Task HandleSingleAudioDownload(
        ITelegramBotClient bot, 
        long chatId, 
        UserDownloadSession session, 
        AudioQualityOption option, 
        string title, 
        CancellationToken ct)
    {
        var progressMessage = await bot.SendMessage(chatId, "🎵 Downloading audio... 0%", cancellationToken: ct);
        var progress = new Progress<double>(p => 
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await bot.EditMessageText(chatId, progressMessage.MessageId, $"🎵 Downloading audio... {p:P0}");
                }
                catch { }
            });
        });

        try
        {
            var result = await _downloadService.DownloadAudioAsync(
                session.Url!, option, title, progress, ct);

            if (result.FromCache)
            {
                await bot.EditMessageText(chatId, progressMessage.MessageId, "⚡ <b>Found in cache!</b> Sending instantly...", parseMode: ParseMode.Html, cancellationToken: ct);
            }

            var filePath = result.FilePath;
            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Length > 50 * 1024 * 1024)
            {
                await bot.EditMessageText(chatId, progressMessage.MessageId, "⚠️ File > 50MB, splitting into parts...", cancellationToken: ct);
                var parts = await _downloadService.SplitVideoAsync(filePath, ct);

                foreach (var part in parts)
                {
                    await using var partStream = File.OpenRead(part);
                    await bot.SendAudio(
                        chatId,
                        InputFile.FromStream(partStream, Path.GetFileName(part)),
                        caption: $"✅ {EscapeHtml(title)} (Part {parts.IndexOf(part) + 1}/{parts.Count})",
                        cancellationToken: ct);
                }

                // Clean up split parts
                foreach (var part in parts)
                {
                    try { File.Delete(part); } catch { }
                }
            }
            else
            {
                await using var fileStream = File.OpenRead(filePath);
                await bot.DeleteMessage(chatId, progressMessage.MessageId, ct);
                await bot.SendAudio(
                    chatId,
                    InputFile.FromStream(fileStream, Path.GetFileName(filePath)),
                    caption: $"✅ {EscapeHtml(title)}",
                    cancellationToken: ct);
            }

            session.CurrentStep = DownloadStep.WaitingForUrl;
            _sessionManager.UpdateSession(session);

            await bot.SendMessage(chatId,
                $"✅ <b>Download complete!</b>\n\nSend another YouTube or Instagram link to continue, or send /cancel.\n\n<b>{EscapeHtml(title)}</b> has been sent.",
                parseMode: ParseMode.Html, cancellationToken: ct);

            // Clean up the temp working file only — never delete a cached copy.
            if (!result.FromCache)
            {
                try { File.Delete(filePath); } catch { }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Audio download failed");
            await bot.EditMessageText(chatId, progressMessage.MessageId, 
                $"❌ <b>Download failed:</b>\n<code>{EscapeHtml(ex.Message)}</code>\n\nSend /start to try again.",
                parseMode: ParseMode.Html, cancellationToken: ct);
            session.CurrentStep = DownloadStep.Error;
            _sessionManager.UpdateSession(session);
        }
    }

    private async Task HandlePlaylistAudioDownload(
        ITelegramBotClient bot, 
        long chatId, 
        UserDownloadSession session, 
        AudioQualityOption option, 
        string playlistTitle, 
        CancellationToken ct)
    {
        var playlistInfo = await _downloadService.GetPlaylistInfoAsync(session.Url!, ct);
        var urls = playlistInfo.VideoUrls;

        var progressMessage = await bot.SendMessage(chatId, $"🎵 Starting audio playlist download (0/{urls.Count})...", cancellationToken: ct);

        var progress = new Progress<(int current, int total, string title)>((p) => 
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await bot.EditMessageText(chatId, progressMessage.MessageId,
                        $"🎵 Downloading audio playlist: [{p.current}/{p.total}]\n{p.title}");
                }
                catch { }
            });
        });

        try
        {
            var playlistDir = await _downloadService.DownloadPlaylistAudioAsync(
                urls, option, playlistTitle, progress, ct);

            await bot.EditMessageText(chatId, progressMessage.MessageId, 
                "📦 Preparing audio files for upload...", cancellationToken: ct);

            // Send files one by one
            var files = Directory.GetFiles(playlistDir, "*.mp3").OrderBy(f => f).ToList();
            int sent = 0;

            foreach (var filePath in files)
            {
                try
                {
                    var fileInfo = new FileInfo(filePath);
                    if (fileInfo.Length > 50 * 1024 * 1024)
                    {
                        var parts = await _downloadService.SplitVideoAsync(filePath, ct);

                        foreach (var part in parts)
                        {
                            await using var partStream = File.OpenRead(part);
                            await bot.SendAudio(
                                chatId,
                                InputFile.FromStream(partStream, Path.GetFileName(part)),
                                caption: $"✅ {EscapeHtml(playlistTitle)} (Part {parts.IndexOf(part) + 1}/{parts.Count})",
                                cancellationToken: ct);
                        }

                        // Clean up split parts
                        foreach (var part in parts)
                        {
                            try { File.Delete(part); } catch { }
                        }
                    }
                    else
                    {
                        await using var fileStream = File.OpenRead(filePath);
                        await bot.SendAudio(
                            chatId,
                            InputFile.FromStream(fileStream, Path.GetFileName(filePath)),
                            caption: $"✅ {EscapeHtml(playlistTitle)}",
                            cancellationToken: ct);
                    
                        sent++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to send audio file: {File}", filePath);
                }
            }

            await bot.SendMessage(chatId,
                $"✅ <b>Audio playlist download complete!</b>\n\n" +
                $"Sent: {sent}/{files.Count} audio tracks\n" +
                $"Failed: {files.Count - sent}\n\nSend another link to continue, or send /cancel.",
                parseMode: ParseMode.Html, cancellationToken: ct);

            session.CurrentStep = DownloadStep.WaitingForUrl;
            _sessionManager.UpdateSession(session);

            // Cleanup
            try { Directory.Delete(playlistDir, true); } catch { }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Audio playlist download failed");
            await bot.EditMessageText(chatId, progressMessage.MessageId, 
                $"❌ <b>Audio playlist download failed:</b>\n<code>{EscapeHtml(ex.Message)}</code>\n\nSend /start to try again.",
                parseMode: ParseMode.Html, cancellationToken: ct);
            session.CurrentStep = DownloadStep.Error;
            _sessionManager.UpdateSession(session);
        }
    }

    private InlineKeyboardMarkup BuildVideoQualityKeyboard(List<VideoQualityOption> options)
    {
        var buttons = new List<InlineKeyboardButton[]>();
        for (int i = 0; i < options.Count; i++)
        {
            buttons.Add(new[]
            {
                InlineKeyboardButton.WithCallbackData(options[i].Label, $"quality:{i}")
            });
        }

        return new InlineKeyboardMarkup(buttons);
    }

    private InlineKeyboardMarkup BuildAudioQualityKeyboard(List<AudioQualityOption> options)
    {
        var buttons = new List<InlineKeyboardButton[]>();
        for (int i = 0; i < options.Count; i++)
        {
            buttons.Add(new[]
            {
                InlineKeyboardButton.WithCallbackData(options[i].Label, $"audioq:{i}")
            });
        }

        return new InlineKeyboardMarkup(buttons);
    }

    private static bool IsYouTubeUrl(Uri uri)
    {
        var host = uri.Host.ToLowerInvariant();
        return host.Contains("youtube.com") || host.Contains("youtu.be");
    }

    private static bool IsInstagramUrl(Uri uri)
    {
        var host = uri.Host.ToLowerInvariant();
        return host.Contains("instagram.com");
    }

    private static bool IsPlaylistUrl(Uri uri)
    {
        return uri.Segments.Contains("playlist") || uri.Query.Contains("list=") || uri.Query.Contains("list%3D");
    }

    private static string EscapeHtml(string text)
    {
        return text.Replace("&", "&amp;")
                   .Replace("<", "&lt;")
                   .Replace(">", "&gt;")
                   .Replace("\"", "&quot;");
    }
}