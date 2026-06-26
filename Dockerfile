# ─── Stage 1: Build ───────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

RUN apt-get update && apt-get install -y \
    git \
    curl \
    wget \
    unzip \
    openjdk-17-jre \
    && rm -rf /var/lib/apt/lists/*

# Install PowerShell for arm64
RUN mkdir -p /opt/microsoft/powershell/7 \
    && cd /opt/microsoft/powershell/7 \
    && wget -q https://github.com/PowerShell/PowerShell/releases/download/v7.6.1/powershell-7.6.1-linux-arm64.tar.gz \
    && tar zxf powershell-7.6.1-linux-arm64.tar.gz \
    && chmod +x pwsh \
    && ln -sf /opt/microsoft/powershell/7/pwsh /usr/local/bin/pwsh \
    && rm powershell-7.6.1-linux-arm64.tar.gz

WORKDIR /src

COPY . .

# Do NOT run git submodule update here.
# Coolify / GitHub Actions should clone repo with submodules before Docker build.
RUN pwsh ./publish.ps1 -profiles framework-dependent-linux-arm64

# Hard fail if ApiServer did not publish.
# This prevents pushing/deploying a broken image without the API server.
RUN set -eux; \
    echo "Checking build output after publish.ps1..."; \
    find /src/build/Release/framework-dependent-linux-arm64 -maxdepth 4 \
        \( -name "ApiServer" -o -name "ApiServer.dll" -o -name "ApiServer.runtimeconfig.json" -o -name "ApiServer.deps.json" \) \
        -exec ls -lah {} \; ; \
    test -f /src/build/Release/framework-dependent-linux-arm64/components/ApiServer.dll; \
    test -f /src/build/Release/framework-dependent-linux-arm64/components/ApiServer.runtimeconfig.json; \
    test -f /src/build/Release/framework-dependent-linux-arm64/components/ApiServer.deps.json


# ─── Stage 2: Runtime ─────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

RUN apt-get update && apt-get install -y \
    openjdk-17-jre \
    curl \
    wget \
    && rm -rf /var/lib/apt/lists/*

# Install PowerShell for arm64
RUN mkdir -p /opt/microsoft/powershell/7 \
    && cd /opt/microsoft/powershell/7 \
    && wget -q https://github.com/PowerShell/PowerShell/releases/download/v7.6.1/powershell-7.6.1-linux-arm64.tar.gz \
    && tar zxf powershell-7.6.1-linux-arm64.tar.gz \
    && chmod +x pwsh \
    && ln -sf /opt/microsoft/powershell/7/pwsh /usr/local/bin/pwsh \
    && rm powershell-7.6.1-linux-arm64.tar.gz

WORKDIR /app

COPY --from=build /src/build/Release/framework-dependent-linux-arm64/ .

# Permissions + ApiServer wrapper.
# If publish produced only ApiServer.dll, create /app/components/ApiServer
# so the launcher validation passes and can start API server.
RUN set -eux; \
    chmod +x ./run_launcher.ps1 2>/dev/null || true; \
    chmod -R +x ./components/ 2>/dev/null || true; \
    chmod +x ./launcher/Launcher 2>/dev/null || true; \
    echo "Checking ApiServer runtime output..."; \
    ls -lah /app/components | grep -E 'ApiServer|Solace.ApiServer' || true; \
    test -f /app/components/ApiServer.dll; \
    test -f /app/components/ApiServer.runtimeconfig.json; \
    test -f /app/components/ApiServer.deps.json; \
    if [ ! -x /app/components/ApiServer ]; then \
        printf '%s\n' \
            '#!/bin/sh' \
            'cd /app/components' \
            'exec dotnet ApiServer.dll "$@"' \
            > /app/components/ApiServer; \
        chmod +x /app/components/ApiServer; \
    fi; \
    test -x /app/components/ApiServer; \
    ls -lah /app/components/ApiServer*

# Ensure persistent directories exist inside container (volumes mount over these)
RUN mkdir -p \
    /app/launcher/Data \
    /app/launcher/logs \
    /app/data \
    /app/logs \
    /app/staticdata/resourcepacks \
    /app/staticdata/server_template_dir \
    /root/.aspnet/DataProtection-Keys

ENV DOTNET_SYSTEM_NET_DISABLEIPV6=1
ENV COMPlus_gcServer=0
ENV COMPlus_gcConcurrent=1
ENV DOTNET_GCHeapHardLimit=536870912
ENV ASPNETCORE_URLS=http://0.0.0.0:5000

EXPOSE 5000 1808 5532 19132/udp

VOLUME ["/app/launcher/Data"]

ENTRYPOINT ["pwsh", "./run_launcher.ps1"]
