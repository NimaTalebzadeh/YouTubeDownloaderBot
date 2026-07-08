using System.Diagnostics;
using YouTubeDownloaderBot.State;

namespace YouTubeDownloaderBot.Services;

public sealed class InstagramDownloadService
{
    private readonly string _tempDirectory;
    private readonly DownloadCacheService _cache;
    private readonly ILogger<InstagramDownloadService> _logger;

    public InstagramDownloadService(DownloadCacheService cache, ILogger<InstagramDownloadService> logger)
    {
        _cache = cache;
        _logger = logger;
        _tempDirectory = Path.Combine(Path.GetTempPath(), "YouTubeDownloaderBot", "instagram");
        Directory.CreateDirectory(_tempDirectory);
    }

    public async Task<VideoInfo> GetInfoAsync(string url, CancellationToken ct)
    {
        // For now, support only one quality: best
        return new VideoInfo
        {
            Title = "Instagram Post",
            VideoOptions = new List<VideoQualityOption>
            {
                new() { Label = "Best Quality", MaxHeight = 1080, NeedsFFmpeg = true }
            },
            AudioOptions = new List<AudioQualityOption>
            {
                new() { Label = "Best Audio", BitrateKbps = 128 }
            }
        };
    }

    public async Task<DownloadResult> DownloadAsync(string url, string title, bool isAudio, CancellationToken ct)
    {
        var fileName = $"{SanitizeFileName(title)}_{Guid.NewGuid()}.{(isAudio ? "mp3" : "mp4")}";
        var outputPath = Path.Combine(_tempDirectory, fileName);
        
        var args = isAudio 
            ? $"-x --audio-format mp3 -o \"{outputPath}\" \"{url}\""
            : $"-f bestvideo+bestaudio/best -o \"{outputPath}\" \"{url}\"";

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "yt-dlp",
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        await process.WaitForExitAsync(ct);
        
        if (process.ExitCode != 0)
            throw new Exception("yt-dlp failed");

        return new DownloadResult(outputPath, FromCache: false);
    }

    private static string SanitizeFileName(string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(fileName.Select(c => invalid.Contains(c) ? '_' : c));
    }
}
