# syntax=docker/dockerfile:1.7

# ---------- Runtime base (with ffmpeg + yt-dlp) ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base

# ffmpeg gives us ffmpeg + ffprobe; ca-certificates + curl needed to fetch yt-dlp.
RUN apt-get update \
 && apt-get install -y --no-install-recommends ffmpeg ca-certificates curl \
 && rm -rf /var/lib/apt/lists/*

# yt-dlp's "linux" build is a PyInstaller bundle — no Python install required.
# Pin to a known-good release tag via build-arg if you want reproducibility;
# default pulls the latest release at image-build time.
ARG YTDLP_VERSION=latest
RUN if [ "$YTDLP_VERSION" = "latest" ]; then \
      curl -fsSL -o /usr/local/bin/yt-dlp https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp_linux ; \
    else \
      curl -fsSL -o /usr/local/bin/yt-dlp https://github.com/yt-dlp/yt-dlp/releases/download/${YTDLP_VERSION}/yt-dlp_linux ; \
    fi \
 && chmod +x /usr/local/bin/yt-dlp \
 && /usr/local/bin/yt-dlp --version

WORKDIR /app
EXPOSE 8080

# ---------- Build ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Soap.csproj ./
RUN dotnet restore Soap.csproj

COPY . .
RUN dotnet publish Soap.csproj -c Release -o /app/publish /p:UseAppHost=false

# ---------- Final ----------
FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .

# Listen on 8080 in the container; map to whatever host port you like.
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true

# Persist downloaded media + settings + cookies across container replacements.
VOLUME /app/Data

ENTRYPOINT ["dotnet", "Soap.dll"]
