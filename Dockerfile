# syntax=docker/dockerfile:1.7

# Stage 1: Build React 19 + TypeScript + Tailwind v4 + @xyflow/react Frontend
FROM --platform=$BUILDPLATFORM node:22-alpine AS frontend-build
WORKDIR /src/frontend
COPY src/Managerr.Frontend/package.json src/Managerr.Frontend/package-lock.json ./
RUN npm ci
COPY src/Managerr.Frontend/ ./
RUN npx tsc -b && npx vite build --outDir /src/frontend/dist

# Stage 2: Build & Publish .NET 10 Modular Monolith (cross-compiled natively on $BUILDPLATFORM)
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS dotnet-build
ARG TARGETARCH
WORKDIR /src
COPY Directory.Build.props ./
COPY src/Managerr.Common/Managerr.Common.csproj src/Managerr.Common/
COPY src/Managerr.Datastore/Managerr.Datastore.csproj src/Managerr.Datastore/
COPY src/Managerr.Security/Managerr.Security.csproj src/Managerr.Security/
COPY src/Managerr.Indexers/Managerr.Indexers.csproj src/Managerr.Indexers/
COPY src/Managerr.Downloaders/Managerr.Downloaders.csproj src/Managerr.Downloaders/
COPY src/Managerr.Metadata/Managerr.Metadata.csproj src/Managerr.Metadata/
COPY src/Managerr.Catalog/Managerr.Catalog.csproj src/Managerr.Catalog/
COPY src/Managerr.Profiles/Managerr.Profiles.csproj src/Managerr.Profiles/
COPY src/Managerr.Verification/Managerr.Verification.csproj src/Managerr.Verification/
COPY src/Managerr.Migration/Managerr.Migration.csproj src/Managerr.Migration/
COPY src/Managerr.Cleanerr/Managerr.Cleanerr.csproj src/Managerr.Cleanerr/
COPY src/Managerr.Host/Managerr.Host.csproj src/Managerr.Host/
RUN arch="${TARGETARCH:-$(uname -m)}" \
 && case "${arch}" in \
      amd64|x86_64) RID="linux-x64" ;; \
      arm64|aarch64) RID="linux-arm64" ;; \
      *) echo "Unsupported architecture: ${arch}" >&2; exit 1 ;; \
    esac \
 && dotnet restore src/Managerr.Host/Managerr.Host.csproj -r "${RID}" -p:AllowMissingPrunePackageData=true
COPY src/Managerr.Common/ src/Managerr.Common/
COPY src/Managerr.Datastore/ src/Managerr.Datastore/
COPY src/Managerr.Security/ src/Managerr.Security/
COPY src/Managerr.Indexers/ src/Managerr.Indexers/
COPY src/Managerr.Downloaders/ src/Managerr.Downloaders/
COPY src/Managerr.Metadata/ src/Managerr.Metadata/
COPY src/Managerr.Catalog/ src/Managerr.Catalog/
COPY src/Managerr.Profiles/ src/Managerr.Profiles/
COPY src/Managerr.Verification/ src/Managerr.Verification/
COPY src/Managerr.Migration/ src/Managerr.Migration/
COPY src/Managerr.Cleanerr/ src/Managerr.Cleanerr/
COPY src/Managerr.Host/ src/Managerr.Host/
RUN arch="${TARGETARCH:-$(uname -m)}" \
 && case "${arch}" in \
      amd64|x86_64) RID="linux-x64" ;; \
      arm64|aarch64) RID="linux-arm64" ;; \
      *) echo "Unsupported architecture: ${arch}" >&2; exit 1 ;; \
    esac \
 && dotnet publish src/Managerr.Host/Managerr.Host.csproj -c Release -r "${RID}" --self-contained false --no-restore -o /app/publish -p:UseAppHost=false -p:AllowMissingPrunePackageData=true
COPY --from=frontend-build /src/frontend/dist /app/publish/wwwroot

# Stage 3: Hardened Zero-External-DB Runtime Image (UID 1000:1000, Embedded SQLite WAL + FTS5 in /config)
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
ARG VERSION="0.2.0"
ARG GIT_SHA=""
LABEL org.opencontainers.image.title="Managerr" \
      org.opencontainers.image.description="Unified self-hosted media lifecycle platform replacing Sonarr, Radarr, Lidarr, Readarr, Prowlarr, and Maintainerr." \
      org.opencontainers.image.source="https://github.com/m0ritz2k19/managerr" \
      org.opencontainers.image.url="https://github.com/m0ritz2k19/managerr" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.revision="${GIT_SHA}" \
      org.opencontainers.image.licenses="GPL-3.0-or-later"

RUN apt-get update && apt-get install -y --no-install-recommends \
    ffmpeg \
    mkvtoolnix \
    mediainfo \
    tesseract-ocr \
    tesseract-ocr-eng \
    tesseract-ocr-deu \
    libgomp1 \
    curl \
    ca-certificates \
  && rm -rf /var/lib/apt/lists/* \
  && mkdir -p /config /data /media /downloads /storage \
  && chown -R 1000:1000 /config /data /media /downloads /storage

WORKDIR /app
COPY --from=dotnet-build /app/publish ./

VOLUME ["/config"]
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
