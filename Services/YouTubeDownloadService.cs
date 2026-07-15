using System.Diagnostics;
using System.Net.Http;
using YoutubeExplode;
using YoutubeExplode.Common;
using YoutubeExplode.Converter;
using YoutubeExplode.Playlists;
using YoutubeExplode.Videos;
using YoutubeExplode.Videos.Streams;
using YouTubeDownloaderBot.State;

namespace YouTubeDownloaderBot.Services;

public sealed class YouTubeDownloadService
{
    private readonly YoutubeClient _youtube;
    private readonly string _tempDirectory;
    private readonly DownloadCacheService _cache;
    private readonly ILogger<YouTubeDownloadService> _logger;

    public YouTubeDownloadService(DownloadCacheService cache, ILogger<YouTubeDownloadService> logger)
    {
        var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        _youtube = new YoutubeClient(httpClient);
        _cache = cache;
        _logger = logger;
        _tempDirectory = Path.Combine(Path.GetTempPath(), "YouTubeDownloaderBot");
        Directory.CreateDirectory(_tempDirectory);
    }

    public async Task<bool> CheckDependenciesAsync()
    {
        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = "-version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task<List<string>> SplitVideoAsync(string inputPath, CancellationToken ct)
    {
        var fileInfo = new FileInfo(inputPath);
        var totalSizeMb = fileInfo.Length / 1_048_576.0;
        
        // If total is <= 50MB, no split needed
        if (totalSizeMb <= 50)
            return new List<string> { inputPath };

        var ext = Path.GetExtension(inputPath).ToLowerInvariant();
        var guid = Guid.NewGuid();
        var outputPattern = Path.Combine(_tempDirectory, $"part_{guid}_%03d{ext}");

        // For audio-only files (mp3, m4a, ogg, etc.), re-encode is needed for segment muxer
        var isAudio = ext is ".mp3" or ".m4a" or ".ogg" or ".wav" or ".flac";
        
        string args;
        if (isAudio)
        {
            // Split audio by time (~10 min per part), re-encode to MP3 with constant bitrate
            args = $"-i \"{inputPath}\" -map 0 -f segment -segment_time 00:10:00 -c:a libmp3lame -q:a 2 -ar 44100 -ac 2 -reset_timestamps 1 \"{outputPattern}\"";
        }
        else
        {
            // Split video by keyframe before 49MB per segment (under Telegram's 50MB limit)
            args = $"-i \"{inputPath}\" -c copy -map 0 -f segment -segment_time 10:00:00 -fs 49M -reset_timestamps 1 \"{outputPattern}\"";
        }

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        
        // Use a combined cancellation token with a 5-minute timeout
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        
        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                try { process.Kill(); } catch { }
            }
            throw new TimeoutException("Splitting timed out after 5 minutes. The file might be corrupt or too long.");
        }

        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync(ct);
            throw new Exception($"Splitting failed: {error}");
        }

        return Directory
            .GetFiles(_tempDirectory, $"part_{guid}_*")
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<VideoInfo> GetVideoInfoAsync(string url, CancellationToken ct)
    {
        var video = await _youtube.Videos.GetAsync(url, ct);
        var manifest = await _youtube.Videos.Streams.GetManifestAsync(url, ct);

        var bestAudio = manifest.GetAudioOnlyStreams().GetWithHighestBitrate() as AudioOnlyStreamInfo;
        var videoOptions = BuildVideoOptions(manifest, bestAudio);
        var audioOptions = BuildAudioOptions(manifest);

        return new VideoInfo
        {
            Title = video.Title,
            Author = video.Author.ToString(),
            Duration = video.Duration,
            VideoOptions = videoOptions,
            AudioOptions = audioOptions
        };
    }

    public async Task<PlaylistInfo> GetPlaylistInfoAsync(string url, CancellationToken ct)
    {
        var playlist = await _youtube.Playlists.GetAsync(url, ct);
        var videos = await _youtube.Playlists.GetVideosAsync(url).CollectAsync();

        return new PlaylistInfo
        {
            Title = playlist.Title ?? "Unknown Playlist",
            Author = playlist.Author?.ToString() ?? "Unknown",
            VideoCount = videos.Count,
            VideoUrls = videos.Select(v => v.Url).ToList()
        };
    }

    public async Task<DownloadResult> DownloadVideoAsync(
        string url,
        VideoQualityOption option,
        string title,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var qualityKey = $"video-{option.MaxHeight}-{(option.NeedsFFmpeg ? "ffmpeg" : "muxed")}";

        // Cache hit — skip YouTube + FFmpeg entirely.
        var cached = await _cache.TryGetAsync(url, "video", qualityKey);
        if (cached != null)
            return new DownloadResult(cached, FromCache: true);

        var manifest = await _youtube.Videos.Streams.GetManifestAsync(url, ct);
        var fileName = SanitizeFileName(title) + ".mp4";
        var outputPath = Path.Combine(_tempDirectory, fileName);

        if (option.NeedsFFmpeg)
        {
            var videoStream = manifest.GetVideoOnlyStreams()
                .Where(s => s.Container == Container.Mp4 && s.VideoQuality.MaxHeight == option.MaxHeight)
                .OrderByDescending(s => s.Bitrate)
                .First();

            var audioStream = manifest.GetAudioOnlyStreams().GetWithHighestBitrate();

            // Step 1: Download video-only stream with progress
            var rawVideoPath = Path.Combine(_tempDirectory, $"raw_video_{Guid.NewGuid()}.mp4");
            var videoProgress = new Progress<double>(p => progress?.Report(p * 0.8)); // 0-80%
            await _youtube.Videos.Streams.DownloadAsync(videoStream, rawVideoPath, videoProgress, ct);

            // Step 2: Download audio-only stream
            var rawAudioPath = Path.Combine(_tempDirectory, $"raw_audio_{Guid.NewGuid()}.mp4");
            var audioProgress = new Progress<double>(p => progress?.Report(0.8 + p * 0.1)); // 80-90%
            await _youtube.Videos.Streams.DownloadAsync(audioStream, rawAudioPath, audioProgress, ct);

            // Report conversion phase
            progress?.Report(0.9);

            // Step 3: Mux with FFmpeg
            var ffmpegArgs = $"-i \"{rawVideoPath}\" -i \"{rawAudioPath}\" -c copy -map 0:v:0 -map 1:a:0 -movflags +faststart \"{outputPath}\"";
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = ffmpegArgs,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0)
            {
                var error = await process.StandardError.ReadToEndAsync(ct);
                throw new Exception($"FFmpeg muxing failed: {error}");
            }

            // Cleanup raw files
            try { File.Delete(rawVideoPath); } catch { }
            try { File.Delete(rawAudioPath); } catch { }
            
            progress?.Report(1.0);
        }
        else
        {
            var muxedStream = manifest.GetMuxedStreams()
                .Where(s => s.VideoQuality.MaxHeight == option.MaxHeight)
                .OrderByDescending(s => s.Bitrate)
                .First();

            await _youtube.Videos.Streams.DownloadAsync(muxedStream, outputPath, progress, ct);
        }

        await _cache.StoreAsync(outputPath, url, "video", qualityKey, fileName);
        return new DownloadResult(outputPath, FromCache: false);
    }

    public async Task<DownloadResult> DownloadAudioAsync(
        string url,
        AudioQualityOption option,
        string title,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var qualityKey = $"audio-{option.BitrateKbps}kbps";

        var cached = await _cache.TryGetAsync(url, "audio", qualityKey);
        if (cached != null)
            return new DownloadResult(cached, FromCache: true);

        var manifest = await _youtube.Videos.Streams.GetManifestAsync(url, ct);

        var audioStreams = manifest.GetAudioOnlyStreams()
            .OrderByDescending(s => s.Bitrate)
            .ToList();

        var stream = audioStreams.FirstOrDefault(s => s.Bitrate.KiloBitsPerSecond == option.BitrateKbps)
            ?? audioStreams.First();

        var fileName = SanitizeFileName(title) + ".mp3";
        var outputPath = Path.Combine(_tempDirectory, fileName);

        // Step 1: Download raw audio stream with progress
        var rawAudioPath = Path.Combine(_tempDirectory, $"raw_audio_{Guid.NewGuid()}.mp4");
        var audioProgress = new Progress<double>(p => progress?.Report(p * 0.8)); // 0-80%
        await _youtube.Videos.Streams.DownloadAsync(stream, rawAudioPath, audioProgress, ct);

        // Report conversion phase
        progress?.Report(0.85);

        // Step 2: Convert to MP3 with FFmpeg
        var ffmpegArgs = $"-i \"{rawAudioPath}\" -vn -codec:a libmp3lame -q:a 2 \"{outputPath}\"";
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = ffmpegArgs,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.Start();
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync(ct);
            throw new Exception($"FFmpeg audio conversion failed: {error}");
        }

        // Cleanup raw file
        try { File.Delete(rawAudioPath); } catch { }

        await _cache.StoreAsync(outputPath, url, "audio", qualityKey, fileName);
        return new DownloadResult(outputPath, FromCache: false);
    }

    public async Task<string> DownloadPlaylistVideosAsync(
        List<string> urls, 
        VideoQualityOption option, 
        string playlistTitle,
        IProgress<(int current, int total, string title)>? progress = null,
        CancellationToken ct = default)
    {
        var playlistDir = Path.Combine(_tempDirectory, SanitizeFileName(playlistTitle));
        Directory.CreateDirectory(playlistDir);

        var downloadedFiles = new List<string>();

        for (int i = 0; i < urls.Count; i++)
        {
            try
            {
                var video = await _youtube.Videos.GetAsync(urls[i], ct);
                progress?.Report((i + 1, urls.Count, video.Title));

                var manifest = await _youtube.Videos.Streams.GetManifestAsync(urls[i], ct);
                var matchingOption = FindMatchingVideoOption(manifest, option);
                
                if (matchingOption == null)
                {
                    _logger.LogWarning("Quality not available for video: {Title}", video.Title);
                    continue;
                }

                var fileName = $"{i + 1:D3}_{SanitizeFileName(video.Title)}.mp4";
                var outputPath = Path.Combine(playlistDir, fileName);

                if (matchingOption.NeedsFFmpeg)
                {
                    var videoStream = manifest.GetVideoOnlyStreams()
                        .Where(s => s.Container == Container.Mp4 && s.VideoQuality.MaxHeight == matchingOption.MaxHeight)
                        .OrderByDescending(s => s.Bitrate)
                        .First();

                    var audioStream = manifest.GetAudioOnlyStreams().GetWithHighestBitrate();

                    var request = new ConversionRequestBuilder(outputPath)
                        .SetContainer(Container.Mp4)
                        .SetPreset(ConversionPreset.UltraFast)
                        .Build();

                    await _youtube.Videos.DownloadAsync(
                        new IStreamInfo[] { videoStream, audioStream }, 
                        request, 
                        null, 
                        ct);
                }
                else
                {
                    var muxedStream = manifest.GetMuxedStreams()
                        .Where(s => s.VideoQuality.MaxHeight == matchingOption.MaxHeight)
                        .OrderByDescending(s => s.Bitrate)
                        .First();

                    await _youtube.Videos.Streams.DownloadAsync(muxedStream, outputPath, null, ct);
                }

                downloadedFiles.Add(outputPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to download video from playlist: {Url}", urls[i]);
            }
        }

        return playlistDir;
    }

    public async Task<string> DownloadPlaylistAudioAsync(
        List<string> urls, 
        AudioQualityOption option, 
        string playlistTitle,
        IProgress<(int current, int total, string title)>? progress = null,
        CancellationToken ct = default)
    {
        var playlistDir = Path.Combine(_tempDirectory, SanitizeFileName(playlistTitle) + "_audio");
        Directory.CreateDirectory(playlistDir);

        for (int i = 0; i < urls.Count; i++)
        {
            try
            {
                var video = await _youtube.Videos.GetAsync(urls[i], ct);
                progress?.Report((i + 1, urls.Count, video.Title));

                var manifest = await _youtube.Videos.Streams.GetManifestAsync(urls[i], ct);
                var audioStreams = manifest.GetAudioOnlyStreams()
                    .OrderByDescending(s => s.Bitrate)
                    .ToList();

                var stream = audioStreams.FirstOrDefault(s => s.Bitrate.KiloBitsPerSecond == option.BitrateKbps)
                    ?? audioStreams.First();

                var fileName = $"{i + 1:D3}_{SanitizeFileName(video.Title)}.mp3";
                var outputPath = Path.Combine(playlistDir, fileName);

                var request = new ConversionRequestBuilder(outputPath)
                    .SetContainer(new Container("mp3"))
                    .SetPreset(ConversionPreset.UltraFast)
                    .Build();

                await _youtube.Videos.DownloadAsync(
                    new IStreamInfo[] { stream }, 
                    request, 
                    null, 
                    ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to download audio from playlist: {Url}", urls[i]);
            }
        }

        return playlistDir;
    }

    private List<VideoQualityOption> BuildVideoOptions(StreamManifest manifest, AudioOnlyStreamInfo? bestAudio)
    {
        var options = new List<VideoQualityOption>();

        var videoOnly = manifest.GetVideoOnlyStreams()
            .Where(s => s.Container == Container.Mp4)
            .OrderByDescending(s => s.VideoQuality.MaxHeight)
            .GroupBy(s => s.VideoQuality.MaxHeight)
            .Select(g => g.First())
            .ToList();

        foreach (var stream in videoOnly)
        {
            var audioSize = bestAudio?.Size.Bytes ?? 0;
            var sizeMb = (stream.Size.Bytes + audioSize) / 1_048_576.0;
            var label = $"{stream.VideoQuality.MaxHeight,5}p  ~{sizeMb,6:F1} MB  [FFmpeg required]";

            options.Add(new VideoQualityOption
            {
                Label = label,
                NeedsFFmpeg = true,
                MaxHeight = stream.VideoQuality.MaxHeight,
                StreamType = "videoOnly",
                VideoSizeBytes = stream.Size.Bytes,
                AudioSizeBytes = audioSize
            });
        }

        var muxed = manifest.GetMuxedStreams()
            .OrderByDescending(s => s.VideoQuality.MaxHeight)
            .ToList();

        foreach (var stream in muxed)
        {
            var sizeMb = stream.Size.Bytes / 1_048_576.0;
            var label = $"{stream.VideoQuality.MaxHeight,5}p  ~{sizeMb,6:F1} MB  (no FFmpeg needed)";

            options.Add(new VideoQualityOption
            {
                Label = label,
                NeedsFFmpeg = false,
                MaxHeight = stream.VideoQuality.MaxHeight,
                StreamType = "muxed",
                VideoSizeBytes = stream.Size.Bytes,
                AudioSizeBytes = 0
            });
        }

        return options;
    }

    private List<AudioQualityOption> BuildAudioOptions(StreamManifest manifest)
    {
        var options = new List<AudioQualityOption>();

        var audioStreams = manifest.GetAudioOnlyStreams()
            .OrderByDescending(s => s.Bitrate)
            .ToList();

        foreach (var stream in audioStreams)
        {
            var sizeMb = stream.Size.Bytes / 1_048_576.0;
            var label = $"{stream.Bitrate.KiloBitsPerSecond,6:F0} kbps  ~{sizeMb:F1} MB";

            options.Add(new AudioQualityOption
            {
                Label = label,
                BitrateKbps = (int)stream.Bitrate.KiloBitsPerSecond,
                SizeBytes = stream.Size.Bytes
            });
        }

        return options;
    }

    private VideoQualityOption? FindMatchingVideoOption(StreamManifest manifest, VideoQualityOption reference)
    {
        var bestAudio = manifest.GetAudioOnlyStreams().GetWithHighestBitrate() as AudioOnlyStreamInfo;
        var options = BuildVideoOptions(manifest, bestAudio);
        return options.FirstOrDefault(o => 
            o.MaxHeight == reference.MaxHeight && o.NeedsFFmpeg == reference.NeedsFFmpeg);
    }

    private static string SanitizeFileName(string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = string.Concat(fileName.Select(c => invalid.Contains(c) ? '_' : c));
        return safe.Length > 100 ? safe[..100] : safe;
    }

    public void CleanupTempFiles()
    {
        try
        {
            if (!Directory.Exists(_tempDirectory))
                return;

            // Delete only loose working files in the temp root — leave subdirectories
            // (notably the persistent cache) untouched.
            foreach (var file in Directory.EnumerateFiles(_tempDirectory))
            {
                try { File.Delete(file); } catch { }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cleanup temp directory");
        }
    }
}

public record VideoInfo
{
    public string Title { get; init; } = "";
    public string Author { get; init; } = "";
    public TimeSpan? Duration { get; init; }
    public List<VideoQualityOption> VideoOptions { get; init; } = new();
    public List<AudioQualityOption> AudioOptions { get; init; } = new();
}

/// <summary>
/// Result of a single-video/audio download. <see cref="FilePath"/> points to a
/// temp working file the caller owns (and should delete after sending).
/// <see cref="FromCache"/> is true when the file was served from the cache and
/// no YouTube/FFmpeg work was performed.
/// </summary>
public sealed record DownloadResult(string FilePath, bool FromCache);

public record PlaylistInfo
{
    public string Title { get; init; } = "";
    public string Author { get; init; } = "";
    public int VideoCount { get; init; }
    public List<string> VideoUrls { get; init; } = new();
}