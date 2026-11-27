# ─── Stage 1: Build ───────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

ARG TARGETARCH
# Commit SHA of the source the image is built from. Passed by CI
# (docker-image*.yml build-args); surfaced to the runtime stage as the OCI
# revision label and the APACE_COMMIT env var, which the panel's update check
# falls back to when the SDK-baked assembly SHA is missing.
ARG SOURCE_COMMIT
# TARGETARCH is injected by docker buildx. Map to our RID suffixes.
# Also detect at runtime for non-buildkit builds.
RUN set -eux; \
    if [ -z "${TARGETARCH:-}" ]; then \
        TARGETARCH=$(uname -m); \
    fi; \
    case "$TARGETARCH" in \
        amd64|x86_64) RID=linux-x64; PWARCH=x64 ;; \
        arm64|aarch64) RID=linux-arm64; PWARCH=arm64 ;; \
        *) echo "Unsupported: $TARGETARCH"; exit 1 ;; \
    esac; \
    echo "RID=$RID PWARCH=$PWARCH" > /tmp/arch.env

# apt can fail transiently (Ubuntu mirror hiccups, e.g. Err: fetching
# libasound2 from security.ubuntu.com -> apt exit 100) - retry before giving up.
RUN set -eux; \
    ok=0; \
    for attempt in 1 2 3; do \
        if apt-get update && apt-get install -y \
            git \
            curl \
            wget \
            unzip \
            openjdk-17-jre \
; then ok=1; break; fi; \
        echo "apt attempt ${attempt} failed - retrying in 10s"; \
        sleep 10; \
    done; \
    [ "$ok" = 1 ] || { echo "apt-get failed after 3 attempts"; exit 100; }; \
    rm -rf /var/lib/apt/lists/*

# Install PowerShell for the target architecture
RUN set -eux; \
    . /tmp/arch.env; \
    mkdir -p /opt/microsoft/powershell/7 \
    && cd /opt/microsoft/powershell/7 \
    && wget -q --tries=5 --retry-connrefused "https://github.com/PowerShell/PowerShell/releases/download/v7.6.1/powershell-7.6.1-linux-${PWARCH}.tar.gz" \
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

# Commit SHA of the source the image was built from (redeclared for this stage).
ARG SOURCE_COMMIT

LABEL org.opencontainers.image.revision=${SOURCE_COMMIT}

RUN set -eux; \
    if [ -z "${TARGETARCH:-}" ]; then \
        TARGETARCH=$(uname -m); \
    fi; \
    case "$TARGETARCH" in \
        amd64|x86_64) RID=linux-x64; PWARCH=x64 ;; \
        arm64|aarch64) RID=linux-arm64; PWARCH=arm64 ;; \
        *) echo "Unsupported: $TARGETARCH"; exit 1 ;; \
    esac; \
    echo "RID=$RID PWARCH=$PWARCH" > /tmp/arch.env

# apt can fail transiently (Ubuntu mirror hiccups, e.g. Err: fetching
# libasound2 from security.ubuntu.com -> apt exit 100) - retry before giving up.
RUN set -eux; \
    ok=0; \
    for attempt in 1 2 3; do \
        if apt-get update && apt-get install -y \
            openjdk-17-jre \
            aapt \
            zipalign \
            curl \
            unzip \
            wget \
; then ok=1; break; fi; \
        echo "apt attempt ${attempt} failed - retrying in 10s"; \
        sleep 10; \
    done; \
    [ "$ok" = 1 ] || { echo "apt-get failed after 3 attempts"; exit 100; }; \
    rm -rf /var/lib/apt/lists/* \
    # aapt ships /usr/bin/aapt AND /usr/bin/aapt2 for both image arches (the
    # base aspnet:10.0 image is Ubuntu, package built from
    # android-platform-frameworks-base for amd64 + arm64). /usr/bin/aapt2 is
    # what the patcher's build falls back to (--aapt) when the aapt2 bundled
    # in apktool.jar fails its pre-flight probe (SIGILL on older ARM CPUs) -
    # hard-fail the image build here rather than discover a missing distro
    # package at patch time.
    && test -x /usr/bin/aapt \
    && test -x /usr/bin/aapt2

# ldid (Link Identity Editor) pseudo-signs the patched iOS binaries so they
# carry the Xbox sign-in (XAL) keychain entitlements - without them the app
# fails sign-in before any login page shows, so its presence is hard-failed
# at image build time. Ubuntu noble ships no apt package for ldid (the apt
# install above broke the image build), so the same pinned ProcursusTeam
# binary MCEPatcher's Ldid would download at runtime is baked into the image
# instead - version and sha256 kept in sync with MCEPatcher.Core/Ldid.cs.
# /usr/local/bin is on PATH, so the patcher's distro lookup finds it and no
# runtime download is needed on player hosts (works offline).
RUN set -eux; \
    . /tmp/arch.env; \
    case "$PWARCH" in \
        x64)   LDID_ASSET=ldid_linux_x86_64;  LDID_SHA256=d7d8c8b06ec913b2778e3e2e0306b5383e83fafe994a3f8acf25f4a525e2a9e9 ;; \
        arm64) LDID_ASSET=ldid_linux_aarch64; LDID_SHA256=8878c8fb918b4047df94e703346913ba92d5901054095e3d5b7dfa192f9a624e ;; \
        *) echo "Unsupported arch for ldid: $PWARCH"; exit 1 ;; \
    esac; \
    curl -fsSL --http1.1 --retry 5 --retry-all-errors --retry-delay 3 \
        -o /usr/local/bin/ldid \
        "https://github.com/ProcursusTeam/ldid/releases/download/v2.1.5-procursus8/${LDID_ASSET}" \
    && echo "${LDID_SHA256}  /usr/local/bin/ldid" | sha256sum -c - \
    && chmod 0755 /usr/local/bin/ldid \
    && test -x /usr/local/bin/ldid

# Docker CLI + compose v2 plugin, installed from the BASE DISTRO's own apt
# repos (docker.io + docker-compose-v2; universe is already enabled in the
# base image's sources — our other apt packages, e.g. aapt, come from there
# too). The panel uses them through the docker socket mounted by
# docker-compose*.yml to run the in-place self-update from its About page
# (SelfUpdateService): "docker compose pull" + "docker compose up -d" on the
# stack it is part of. No daemon runs inside the container — only the mounted
# host socket is used (docker.io ships the engine binaries, but nothing here
# starts dockerd). This is root-equivalent access to the host by design — see
# the SECURITY note on the socket mount in the compose files.
# Deliberately NOT Docker's download.docker.com repo: its URL is distro- and
# suite-specific, and a hardcoded suite has broken CI when the base image
# didn't match (e.g. debian repo with a noble codename). Distro packages
# always match the base image.
RUN set -eux; \
    apt-get update; \
    apt-get install -y --no-install-recommends docker.io docker-compose-v2; \
    rm -rf /var/lib/apt/lists/*; \
    docker --version; \
    docker compose version

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

# Android platform 29 framework (android.jar) for apktool.
# apktool 3.x bundles its own framework (1.apk), but its resources table is
# rejected by the aapt2 shipped in Debian ("RES_TABLE_TYPE_TYPE entry offsets
# overlap actual entry data") during the link step, which fails the build.
# Google's original android.jar for API 29 (Minecraft Earth's targetSdk) is
# well-formed and works. apktool gets it via --frame-path (decode + build),
# enabled by APKTOOL_FRAMEWORK_DIR below.
RUN mkdir -p /opt/android-framework \
    && curl -fsSL --http1.1 --retry 5 --retry-all-errors --retry-delay 3 -o /tmp/platform-29.zip https://dl.google.com/android/repository/platform-29_r05.zip \
    && unzip -j /tmp/platform-29.zip 'android-10/android.jar' -d /opt/android-framework \
    && mv /opt/android-framework/android.jar /opt/android-framework/1.apk \
    && rm /tmp/platform-29.zip \
    && ls -la /opt/android-framework

# Install PowerShell for the target architecture
RUN set -eux; \
    . /tmp/arch.env; \
    mkdir -p /opt/microsoft/powershell/7 \
    && cd /opt/microsoft/powershell/7 \
    && wget -q --tries=5 --retry-connrefused "https://github.com/PowerShell/PowerShell/releases/download/v7.6.1/powershell-7.6.1-linux-${PWARCH}.tar.gz" \
    && tar zxf "powershell-7.6.1-linux-${PWARCH}.tar.gz" \
    && chmod +x pwsh \
    && ln -sf /opt/microsoft/powershell/7/pwsh /usr/local/bin/pwsh \
    && rm "powershell-7.6.1-linux-${PWARCH}.tar.gz"

WORKDIR /app

COPY --from=build /src/build/Release/latest/ .

# Compose files, baked in for the panel's in-place self-update
# (SelfUpdateService picks the one matching APACE_CHANNEL and runs
# "docker compose -f /app/compose/<file> pull && up -d" on the running stack).
COPY docker-compose.yml docker-compose.dev.yml /app/compose/

# Save baked-in mods to a backup location so they survive volume mounts.
# At runtime, volume mounts (like Coolify's server-template-dir) override
# the baked-in /app/staticdata/server_template_dir, making mods invisible.
# We stash them in /app/defaults/mods/ and the entrypoint seeds them
# into the volume-mounted directory on every container start.
RUN if [ -d /app/staticdata/server_template_dir/mods ]; then \
        mkdir -p /app/defaults && \
        cp -r /app/staticdata/server_template_dir/mods /app/defaults/mods; \
    fi
RUN if [ -d /app/staticdata/server_template_dir/datapacks ]; then \
        mkdir -p /app/defaults && \
        cp -r /app/staticdata/server_template_dir/datapacks /app/defaults/datapacks; \
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
        'SRC_DATAPACKS="/app/defaults/datapacks"' \
        'DST_DATAPACKS="/app/staticdata/server_template_dir/datapacks"' \
        '' \
        'if [ -d "$SRC_DATAPACKS" ]; then' \
        '    mkdir -p "$DST_DATAPACKS"' \
        '    for pack in "$SRC_DATAPACKS"/*; do' \
        '        [ -e "$pack" ] || continue' \
        '        name="$(basename "$pack")"' \
        '        rm -rf "$DST_DATAPACKS/$name"' \
        '        cp -r "$pack" "$DST_DATAPACKS/$name"' \
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
    ls -lah /app/components | grep -E 'ApiServer|Apace.ApiServer' || true; \
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
    ZIPALIGN_PATH=/usr/bin/zipalign \
    APKTOOL_FRAMEWORK_DIR=/opt/android-framework
ENV DOTNET_SYSTEM_NET_DISABLEIPV6=1
ENV COMPlus_gcServer=0
ENV COMPlus_gcConcurrent=1
ENV DOTNET_GCHeapHardLimit=536870912
ENV ASPNETCORE_URLS=http://0.0.0.0:5000
# Consumed by the panel's update check when the assembly's baked-in commit SHA
# is missing or truncated (see UpdateCheckService).
ENV APACE_COMMIT=${SOURCE_COMMIT}

EXPOSE 5000 1808 5532 19132/udp

VOLUME ["/app/launcher/Data"]

ENTRYPOINT ["/app/entrypoint.sh"]
