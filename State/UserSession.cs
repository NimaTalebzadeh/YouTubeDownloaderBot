namespace YouTubeDownloaderBot.State;

public enum DownloadStep
{
    Start,
    WaitingForUrl,
    WaitingForType,
    WaitingForVideoQuality,
    WaitingForAudioQuality,
    Downloading,
    Done,
    Error
}

public enum DownloadType
{
    None,
    Video,
    Audio
}

public enum SourcePlatform
{
    YouTube,
    Instagram
}

public record UserDownloadSession
{
    public long UserId { get; init; }
    public DownloadStep CurrentStep { get; set; } = DownloadStep.Start;
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;
    
    public string? Url { get; set; }
    public SourcePlatform Platform { get; set; } = SourcePlatform.YouTube;
    public bool IsPlaylist { get; set; }
    public DownloadType DownloadType { get; set; } = DownloadType.None;
    public int SelectedQualityIndex { get; set; } = -1;
    
    public string? VideoTitle { get; set; }
    public List<VideoQualityOption>? VideoOptions { get; set; }
    public List<AudioQualityOption>? AudioOptions { get; set; }
}

public record VideoQualityOption
{
    public string Label { get; init; } = "";
    public bool NeedsFFmpeg { get; init; }
    public int MaxHeight { get; init; }
    public string StreamType { get; init; } = ""; // "muxed" or "videoOnly"
    public long VideoSizeBytes { get; init; }
    public long AudioSizeBytes { get; init; }
}

public record AudioQualityOption
{
    public string Label { get; init; } = "";
    public int BitrateKbps { get; init; }
    public long SizeBytes { get; init; }
}

public class UserSessionManager
{
    private readonly Dictionary<long, UserDownloadSession> _sessions = new();
    private readonly TimeSpan _sessionTimeout = TimeSpan.FromMinutes(30);
    private readonly object _lock = new();

    public UserDownloadSession GetOrCreateSession(long userId)
    {
        lock (_lock)
        {
            CleanupExpiredSessions();
            
            if (!_sessions.TryGetValue(userId, out var session))
            {
                session = new UserDownloadSession { UserId = userId };
                _sessions[userId] = session;
            }
            
            session.LastActivity = DateTime.UtcNow;
            return session;
        }
    }

    public bool TryGetSession(long userId, out UserDownloadSession session)
    {
        lock (_lock)
        {
            CleanupExpiredSessions();
            return _sessions.TryGetValue(userId, out session!);
        }
    }

    public void ResetSession(long userId)
    {
        lock (_lock)
        {
            _sessions.Remove(userId);
        }
    }

    public void UpdateSession(UserDownloadSession session)
    {
        lock (_lock)
        {
            session.LastActivity = DateTime.UtcNow;
            _sessions[session.UserId] = session;
        }
    }

    private void CleanupExpiredSessions()
    {
        var cutoff = DateTime.UtcNow - _sessionTimeout;
        var expiredKeys = _sessions
            .Where(kvp => kvp.Value.LastActivity < cutoff)
            .Select(kvp => kvp.Key)
            .ToList();
        
        foreach (var key in expiredKeys)
        {
            _sessions.Remove(key);
        }
    }
}