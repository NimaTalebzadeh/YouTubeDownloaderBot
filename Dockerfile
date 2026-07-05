FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Install FFmpeg
RUN apt-get update && apt-get install -y ffmpeg && rm -rf /var/lib/apt/lists/*

# Copy the csproj file to the WORKDIR /src
# This assumes YouTubeDownloaderBot.csproj is at the root of the build context
COPY YouTubeDownloaderBot.csproj .
RUN dotnet restore YouTubeDownloaderBot.csproj

# Copy all other project files
COPY . .

# Publish the application from the root of the project in /src
# The csproj is in the current directory now.
RUN dotnet publish YouTubeDownloaderBot.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Install FFmpeg in runtime image
RUN apt-get update && apt-get install -y ffmpeg && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .
EXPOSE 5003

ENTRYPOINT ["dotnet", "YouTubeDownloaderBot.dll"]