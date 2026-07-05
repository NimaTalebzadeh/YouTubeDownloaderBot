FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Install FFmpeg
RUN apt-get update && apt-get install -y ffmpeg && rm -rf /var/lib/apt/lists/*

COPY ["YouTubeDownloaderBot/YouTubeDownloaderBot.csproj", "YouTubeDownloaderBot/"]
RUN dotnet restore "YouTubeDownloaderBot/YouTubeDownloaderBot.csproj"

COPY . .
WORKDIR "/src/YouTubeDownloaderBot"
RUN dotnet publish "YouTubeDownloaderBot.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Install FFmpeg in runtime image
RUN apt-get update && apt-get install -y ffmpeg && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .
EXPOSE 5000

ENTRYPOINT ["dotnet", "YouTubeDownloaderBot.dll"]