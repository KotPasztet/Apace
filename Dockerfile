# ─── Stage 1: Build ───────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

ARG TARGETARCH
# TARGETARCH is injected by docker buildx. Map to our RID suffixes.
# Also detect at runtime for non-buildkit builds.
RUN set -eux; \
    if [ -z "$TARGETARCH" ]; then \
        TARGETARCH=$(uname -m); \
    fi; \
    case "$TARGETARCH" in \
        amd64|x86_64) RID=linux-x64; PWARCH=x64 ;; \
        arm64|aarch64) RID=linux-arm64; PWARCH=arm64 ;; \
        *) echo "Unsupported: $TARGETARCH"; exit 1 ;; \
    esac; \
    echo "RID=$RID PWARCH=$PWARCH" > /tmp/arch.env

RUN apt-get update && apt-get install -y \
    git \
    curl \
    wget \
    unzip \
    openjdk-17-jre \
    && rm -rf /var/lib/apt/lists/*

# Install PowerShell for the target architecture
RUN set -eux; \
    . /tmp/arch.env; \
    mkdir -p /opt/microsoft/powershell/7 \
    && cd /opt/microsoft/powershell/7 \
    && wget -q "https://github.com/PowerShell/PowerShell/releases/download/v7.6.1/powershell-7.6.1-linux-${PWARCH}.tar.gz" \
    && tar zxf "powershell-7.6.1-linux-${PWARCH}.tar.gz" \
    && chmod +x pwsh \
    && ln -sf /opt/microsoft/powershell/7/pwsh /usr/local/bin/pwsh \
    && rm "powershell-7.6.1-linux-${PWARCH}.tar.gz"

WORKDIR /src

COPY . .

# Do NOT run git submodule update here.
# Coolify / GitHub Actions should clone repo with submodules before Docker build.
RUN set -eux; \
    . /tmp/arch.env; \
    pwsh ./publish.ps1 -profiles "framework-dependent-${RID}"

# Hard fail if ApiServer did not publish.
# This prevents pushing/deploying a broken image without the API server.
RUN set -eux; \
    . /tmp/arch.env; \
    echo "Checking build output after publish.ps1..."; \
    find "/src/build/Release/framework-dependent-${RID}" -maxdepth 4 \
        \( -name "ApiServer" -o -name "ApiServer.dll" -o -name "ApiServer.runtimeconfig.json" -o -name "ApiServer.deps.json" \) \
        -exec ls -lah {} \; ; \
    test -f "/src/build/Release/framework-dependent-${RID}/components/ApiServer.dll"; \
    test -f "/src/build/Release/framework-dependent-${RID}/components/ApiServer.runtimeconfig.json"; \
    test -f "/src/build/Release/framework-dependent-${RID}/components/ApiServer.deps.json"; \
    test -f "/src/build/Release/framework-dependent-${RID}/launcher/Launcher"; \
    test -f "/src/build/Release/framework-dependent-${RID}/launcher/Launcher.dll"; \
    ln -sfn "framework-dependent-${RID}" /src/build/Release/latest


# ─── Stage 2: Runtime ─────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

ARG TARGETARCH

RUN set -eux; \
    if [ -z "$TARGETARCH" ]; then \
        TARGETARCH=$(uname -m); \
    fi; \
    case "$TARGETARCH" in \
        amd64|x86_64) RID=linux-x64; PWARCH=x64 ;; \
        arm64|aarch64) RID=linux-arm64; PWARCH=arm64 ;; \
        *) echo "Unsupported: $TARGETARCH"; exit 1 ;; \
    esac; \
    echo "RID=$RID PWARCH=$PWARCH" > /tmp/arch.env

RUN apt-get update && apt-get install -y \
    openjdk-17-jre \
    aapt \
    zipalign \
    curl \
    wget \
    && rm -rf /var/lib/apt/lists/*

# aapt2 wrapper (generated inline, like entrypoint.sh).
# apktool 3.x passes a few modern aapt2 flags unconditionally - most notably
# --no-compile-sdk-metadata - but the aapt2 shipped in Debian (package "aapt")
# is older and aborts the link step with "unknown option". The wrapper drops
# those flags and execs the real binary. Dropping them is safe: they only
# DISABLE optional behaviour (compile-sdk metadata injection, compact resource
# entries, feature-flag encoding) that the older aapt2 never had in the first
# place, so the produced APK is equivalent.
RUN printf '%s\n' \
        '#!/bin/bash' \
        '# aapt2 wrapper: filters flags unsupported by this aapt2 build.' \
        '# See comment on the RUN step that generated this file.' \
        'set -eu' \
        'REAL_AAPT2=/usr/bin/aapt2' \
        'args=()' \
        'while [ $# -gt 0 ]; do' \
        '    case "$1" in' \
        '        --no-compile-sdk-metadata|--enable-compact-entries)' \
        '            printf "aapt2-wrapper: dropping unsupported flag %s\n" "$1" >&2' \
        '            shift' \
        '            ;;' \
        '        --feature-flags)' \
        '            printf "aapt2-wrapper: dropping unsupported flag %s (and its value)\n" "$1" >&2' \
        '            if [ $# -ge 2 ]; then shift 2; else shift; fi' \
        '            ;;' \
        '        *)' \
        '            args+=("$1")' \
        '            shift' \
        '            ;;' \
        '    esac' \
        'done' \
        'exec "$REAL_AAPT2" ${args[@]+"${args[@]}"}' \
        > /usr/local/bin/aapt2 \
    && chmod +x /usr/local/bin/aapt2 \
    && /usr/local/bin/aapt2 version

# Install PowerShell for the target architecture
RUN set -eux; \
    . /tmp/arch.env; \
    mkdir -p /opt/microsoft/powershell/7 \
    && cd /opt/microsoft/powershell/7 \
    && wget -q "https://github.com/PowerShell/PowerShell/releases/download/v7.6.1/powershell-7.6.1-linux-${PWARCH}.tar.gz" \
    && tar zxf "powershell-7.6.1-linux-${PWARCH}.tar.gz" \
    && chmod +x pwsh \
    && ln -sf /opt/microsoft/powershell/7/pwsh /usr/local/bin/pwsh \
    && rm "powershell-7.6.1-linux-${PWARCH}.tar.gz"

WORKDIR /app

COPY --from=build /src/build/Release/latest/ .

# Save baked-in mods to a backup location so they survive volume mounts.
# At runtime, volume mounts (like Coolify's server-template-dir) override
# the baked-in /app/staticdata/server_template_dir, making mods invisible.
# We stash them in /app/defaults/mods/ and the entrypoint seeds them
# into the volume-mounted directory on every container start.
RUN if [ -d /app/staticdata/server_template_dir/mods ]; then \
        mkdir -p /app/defaults && \
        cp -r /app/staticdata/server_template_dir/mods /app/defaults/mods; \
    fi

# Generate entrypoint inline (no external file — avoids COPY failures).
# Syncs Fabric mods from /app/defaults/mods/ into the volume-mounted
# /app/staticdata/server_template_dir/mods/ on every container start:
# a bind mount shadows image content, so mods updated in a new image must
# be copied over (and overwrite) the stale copies inside the volume,
# otherwise the persistent Fabric server keeps running the old mod.
RUN printf '%s\n' \
        '#!/bin/sh' \
        'set -eu' \
        '' \
        'SRC_MODS="/app/defaults/mods"' \
        'DST_MODS="/app/staticdata/server_template_dir/mods"' \
        '' \
        'if [ -d "$SRC_MODS" ]; then' \
        '    mkdir -p "$DST_MODS"' \
        '    for jar in "$SRC_MODS"/*.jar; do' \
        '        [ -f "$jar" ] || continue' \
        '        name="$(basename "$jar")"' \
        '        if [ ! -f "$DST_MODS/$name" ] || ! cmp -s "$jar" "$DST_MODS/$name"; then' \
        '            echo "entrypoint: updating mod $name in server_template_dir..."' \
        '            cp -f "$jar" "$DST_MODS/$name"' \
        '        fi' \
        '    done' \
        'fi' \
        '' \
        'exec pwsh /app/run_launcher.ps1' \
        > /app/entrypoint.sh \
    && chmod +x /app/entrypoint.sh

# Permissions + ApiServer wrapper.
# If publish produced only ApiServer.dll, create /app/components/ApiServer
# so the launcher validation passes and can start API server.
RUN set -eux; \
    chmod +x ./run_launcher.ps1 2>/dev/null || true; \
    chmod -R +x ./components/ 2>/dev/null || true; \
    echo "Checking Launcher binary..."; \
    test -f /app/launcher/Launcher; \
    chmod +x /app/launcher/Launcher; \
    test -f /app/launcher/Launcher.dll; \
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

# Native Android build tools for the client patcher (the 'aapt' package
# provides /usr/bin/aapt2). apktool 3.x and Google's build-tools ship x86-64
# Linux binaries only, which cannot run on ARM64 hosts; MCEPatcher.Core
# (SystemTools) picks these env vars up and passes the binary to apktool
# (--aapt) and uses it for zipalign instead of the downloaded build-tools.
# AAPT2_PATH points at the wrapper generated above, which drops aapt2 flags
# unknown to Debian's aapt2 build (see the wrapper RUN step).
ENV AAPT2_PATH=/usr/local/bin/aapt2 \
    ZIPALIGN_PATH=/usr/bin/zipalign
ENV DOTNET_SYSTEM_NET_DISABLEIPV6=1
ENV COMPlus_gcServer=0
ENV COMPlus_gcConcurrent=1
ENV DOTNET_GCHeapHardLimit=536870912
ENV ASPNETCORE_URLS=http://0.0.0.0:5000

EXPOSE 5000 1808 5532 19132/udp

VOLUME ["/app/launcher/Data"]

ENTRYPOINT ["/app/entrypoint.sh"]
