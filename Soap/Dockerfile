# syntax=docker/dockerfile:1.7

# Refresh this stage with --no-cache-filter media-tools to pick up new releases.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS media-tools
ARG TARGETARCH
ARG YTDLP_VERSION=latest
ARG FFMPEG_VERSION=latest
RUN apt-get update \
 && apt-get install -y --no-install-recommends python3 ca-certificates \
 && rm -rf /var/lib/apt/lists/*
COPY docker/install-media-tools.py /tmp/install-media-tools.py
RUN TARGETARCH="$TARGETARCH" YTDLP_VERSION="$YTDLP_VERSION" FFMPEG_VERSION="$FFMPEG_VERSION" \
    python3 /tmp/install-media-tools.py

# ---------- Runtime base (with current stable ffmpeg + ffprobe + yt-dlp) ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
COPY --from=media-tools /tools/ /usr/local/bin/
RUN /usr/local/bin/yt-dlp --version \
 && /usr/local/bin/ffmpeg -version \
 && /usr/local/bin/ffprobe -version
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
