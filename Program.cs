using Serilog;
using Telegram.Bot;
using YouTubeDownloaderBot.Services;
using YouTubeDownloaderBot.State;
using YouTubeDownloaderBot.Telegram;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls("http://0.0.0.0:" + (Environment.GetEnvironmentVariable("PORT") ?? "5000"));

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .CreateLogger();

builder.Host.UseSerilog();

builder.Services.AddSingleton<UserSessionManager>();
builder.Services.AddSingleton<DownloadCacheService>();
builder.Services.AddSingleton<YouTubeDownloadService>();
builder.Services.AddSingleton<ConversationHandler>();
builder.Services.AddSingleton<BotUpdateRouter>();

builder.Services.AddSingleton<ITelegramBotClient>(sp =>
{
    var token = Environment.GetEnvironmentVariable("TELEGRAM_BOTTOKEN")
                ?? builder.Configuration["Telegram:BotToken"];
    if (string.IsNullOrWhiteSpace(token))
    {
        throw new InvalidOperationException("Telegram bot token is not configured. Set TELEGRAM_BOTTOKEN env var or Telegram:BotToken in config.");
    }
    return new TelegramBotClient(token);
});

var app = builder.Build();

app.UseSerilogRequestLogging();

// Check FFmpeg on startup
var downloadService = app.Services.GetRequiredService<YouTubeDownloadService>();
var ffmpegAvailable = await downloadService.CheckDependenciesAsync();
if (!ffmpegAvailable)
{
    Log.Warning("FFmpeg not found in PATH. Video merging and audio conversion will not work!");
    Log.Warning("Install FFmpeg: apt-get install ffmpeg (Debian/Ubuntu) or apk add ffmpeg (Alpine)");
}
else
{
    Log.Information("FFmpeg found and available");
}

var botClient = app.Services.GetRequiredService<ITelegramBotClient>();
var cts = new CancellationTokenSource();

_ = Task.Run(async () =>
{
    var me = await botClient.GetMe(cts.Token);
    Log.Information("YouTube Downloader Bot started as @{Username}", me.Username);

    var offset = 0;
    while (!cts.Token.IsCancellationRequested)
    {
        try
        {
            var updates = await botClient.GetUpdates(
                offset: offset,
                timeout: 30,
                cancellationToken: cts.Token);

            foreach (var update in updates)
            {
                offset = update.Id + 1;
                _ = Task.Run(async () =>
                {
                    var router = app.Services.GetRequiredService<BotUpdateRouter>();
                    var scopedBot = app.Services.GetRequiredService<ITelegramBotClient>();
                    await router.HandleAsync(scopedBot, update, cts.Token);
                });
            }
        }
        catch (OperationCanceledException)
        {
            break;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in polling loop");
            await Task.Delay(5000, cts.Token);
        }
    }
});

app.Lifetime.ApplicationStopping.Register(() =>
{
    cts.Cancel();
    Log.Information("Bot shutting down...");
});

app.Map("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

app.Run();