# syntax=docker/dockerfile:1.7

# Stage 1: Build React 19 + TypeScript + Tailwind v4 + @xyflow/react Frontend
FROM node:22-alpine AS frontend-build
WORKDIR /src/frontend
COPY src/Managerr.Frontend/package.json src/Managerr.Frontend/package-lock.json ./
RUN npm ci
COPY src/Managerr.Frontend/ ./
RUN npx tsc -b && npx vite build --outDir /src/frontend/dist

# Stage 2: Build & Publish .NET 10 Modular Monolith
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS dotnet-build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/ ./src/
RUN dotnet publish src/Managerr.Host/Managerr.Host.csproj -c Release -o /app/publish /p:UseAppHost=false
COPY --from=frontend-build /src/frontend/dist /app/publish/wwwroot

# Stage 3: Hardened Zero-External-DB Runtime Image (UID 1000:1000, Embedded SQLite WAL + FTS5 in /config)
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
LABEL org.opencontainers.image.title="Managerr" \
      org.opencontainers.image.description="Unified self-hosted media lifecycle platform replacing Sonarr, Radarr, Lidarr, Readarr, Prowlarr, and Maintainerr." \
      org.opencontainers.image.source="https://github.com/m0ritz2k19/managerr" \
      org.opencontainers.image.url="https://github.com/m0ritz2k19/managerr" \
      org.opencontainers.image.licenses="GPL-3.0-or-later"

RUN apt-get update && apt-get install -y --no-install-recommends \
    ffmpeg \
    mkvtoolnix \
    mediainfo \
    tesseract-ocr \
    tesseract-ocr-eng \
    tesseract-ocr-deu \
    curl \
    ca-certificates \
  && rm -rf /var/lib/apt/lists/* \
  && mkdir -p /config /data /media /downloads /storage \
  && chown -R 1000:1000 /config /data /media /downloads /storage

WORKDIR /app
COPY --chown=1000:1000 --from=dotnet-build /app/publish ./

VOLUME ["/config"]
ARG GIT_SHA=""
ENV ASPNETCORE_URLS="http://+:8989" \
    Managerr__ConfigDirectory="/config" \
    MANAGERR_GIT_SHA="${GIT_SHA}" \
    HOME="/config" \
    DOTNET_CLI_HOME="/config"

USER 1000:1000
EXPOSE 8989

HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
  CMD curl -fsS http://127.0.0.1:8989/api/v1/system/health || exit 1

ENTRYPOINT ["dotnet", "Managerr.Host.dll"]
