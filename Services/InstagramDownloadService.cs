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
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "yt-dlp",
                Arguments = $"--dump-json --no-warnings \"{url}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
            throw new Exception("yt-dlp failed to fetch Instagram info");

        using var doc = System.Text.Json.JsonDocument.Parse(output);
        var root = doc.RootElement;

        var title = root.TryGetProperty("title", out var titleProp)
            ? titleProp.GetString() ?? "Instagram Post"
            : "Instagram Post";

        var author = root.TryGetProperty("uploader", out var uploaderProp)
            ? uploaderProp.GetString() ?? "Instagram"
            : "Instagram";

        TimeSpan? duration = null;
        if (root.TryGetProperty("duration", out var durationProp) && durationProp.ValueKind == System.Text.Json.JsonValueKind.Number)
        {
            duration = TimeSpan.FromSeconds(durationProp.GetDouble());
        }

        var videoOptions = new List<VideoQualityOption>();

        if (root.TryGetProperty("formats", out var formatsProp) && formatsProp.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            var addedHeights = new HashSet<int>();

            foreach (var format in formatsProp.EnumerateArray()
                         .Where(f => f.TryGetProperty("height", out _))
                         .OrderByDescending(f => f.GetProperty("height").GetInt32()))
            {
                var height = format.GetProperty("height").GetInt32();
                if (height <= 0 || !addedHeights.Add(height))
                    continue;

                long sizeBytes = 0;
                if (format.TryGetProperty("filesize", out var sizeProp) && sizeProp.ValueKind == System.Text.Json.JsonValueKind.Number)
                {
                    sizeBytes = sizeProp.GetInt64();
                }

                var sizeMb = sizeBytes > 0 ? sizeBytes / 1_048_576.0 : 0;
                var label = sizeMb > 0
                    ? $"{height}p  ~{sizeMb:F1} MB"
                    : $"{height}p";

                videoOptions.Add(new VideoQualityOption
                {
                    Label = label,
                    MaxHeight = height,
                    NeedsFFmpeg = false,
                    StreamType = "instagram",
                    VideoSizeBytes = sizeBytes
                });
            }
        }

        if (videoOptions.Count == 0)
        {
            videoOptions.Add(new VideoQualityOption
            {
                Label = "Best Quality",
                MaxHeight = 1080,
                NeedsFFmpeg = false,
                StreamType = "instagram"
            });
        }

        return new VideoInfo
        {
            Title = title,
            Author = author,
            Duration = duration,
            VideoOptions = videoOptions,
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
