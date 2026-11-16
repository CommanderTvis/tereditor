# syntax=docker/dockerfile:1
# The headless tereditor image: `tereditor-headless mcp` over stdio. The contract is in src/Tereditor.Host.Headless/SPEC.md.
#
# tereditor-headless is a NativeAOT binary (NativeAot.props). Linux links with the host's clang, so every
# publish runs under scripts/mise, from a Linux or a macOS host. CI publishes outside Docker, once per architecture, and
# builds the default target (prebuilt):
#   sh scripts/mise x -- dotnet publish src/Tereditor.Host.Headless -c Release -r linux-<arch> -o artifacts/headless/linux-<arch>
#   docker build -f docker/headless/Dockerfile --build-arg PUBLISH_DIR=artifacts/headless/linux-<arch> -t tereditor .
# For local use from a checkout, the "local" target publishes inside the SDK image instead, through the same scripts/mise:
#   docker build -f docker/headless/Dockerfile --target local -t tereditor .
#
# Base images are pinned by digest (the multi-architecture index, so amd64 and arm64 stay in step). To update one, read the
# new digest of its tag (`docker buildx imagetools inspect <image>:<tag>`) and replace the digest; the headless check fails
# on a base image without one.

FROM mcr.microsoft.com/dotnet/sdk:10.0.403@sha256:212fe4cb74627d5adf9817a4088906ae3edff1bd853bb27565d4b46731efe61e AS publish
ARG TARGETARCH
WORKDIR /src
COPY . .
RUN case "$TARGETARCH" in amd64) rid=linux-x64 ;; arm64) rid=linux-arm64 ;; *) echo "unsupported architecture $TARGETARCH" >&2; exit 1 ;; esac \
    && sh scripts/mise x -- dotnet publish src/Tereditor.Host.Headless -c Release -r "$rid" -o /out

# clangd for linux/arm64. clangd/clangd publishes no arm64 build; LLVM's release has one inside its 1.1 GB toolchain archive, too big to
# download at run time. This stage downloads it once at build time, checks the pinned SHA-256 before unpacking, and keeps only bin/clangd and
# the builtin headers it reads from lib/clang/<major>/include. The URL and hash are the linux-aarch64 pin in ClangdPlugin.cs (a check compares them).
# The host copies the result into /cache/managed on first use (ArtifactInstaller.Preinstalled). On amd64 the stage is empty: the host downloads
# the clangd/clangd zip itself.
FROM ubuntu:24.04@sha256:fe81560990fae86dc4743202b2cb4d3df98d0bcf949173cecef15ce1127ef852 AS clangd
ARG TARGETARCH
ARG CLANGD_ARM64_URL=https://github.com/llvm/llvm-project/releases/download/llvmorg-23.1.5/LLVM-23.1.5-Linux-ARM64.tar.zst
ARG CLANGD_ARM64_SHA256=830ceb70edf63849ce66e97bf85310a3bf2bdd04c860a9f2fa4ef1560e290cef
RUN mkdir -p /out \
    && if [ "$TARGETARCH" = arm64 ]; then \
         apt-get update && apt-get install -y --no-install-recommends ca-certificates curl zstd \
         && curl -fsSL --retry 3 -o /tmp/llvm.tar.zst "$CLANGD_ARM64_URL" \
         && echo "$CLANGD_ARM64_SHA256  /tmp/llvm.tar.zst" | sha256sum -c - \
         && zstd -dc --long=30 /tmp/llvm.tar.zst | tar -x -C /out --strip-components=1 --wildcards '*/bin/clangd' '*/lib/clang/*/include' \
         && test -x /out/bin/clangd && ls /out/lib/clang/*/include/stddef.h \
         && printf '{"sha256":"%s","url":"%s"}' "$CLANGD_ARM64_SHA256" "$CLANGD_ARM64_URL" > /out/.tereditor-artifact.json \
         && rm -f /tmp/llvm.tar.zst; \
       fi

# The native binary still loads libicu (globalization) and libssl (HTTPS for installs) at run time, and the .NET SDK it
# downloads for language servers needs those and libstdc++ and zlib too. The chiseled runtime-deps base has libssl, libstdc++
# and the CA certificates, but no libicu, zlib, tzdata or git, and no shell or package manager. The native-deps stage (noble)
# supplies the rest: the files of those packages, and the shared libraries git links that the base lacks, at the same paths.
FROM ubuntu:24.04@sha256:fe81560990fae86dc4743202b2cb4d3df98d0bcf949173cecef15ce1127ef852 AS native-deps
RUN apt-get update \
    && apt-get install -y --no-install-recommends git libicu74 tzdata zlib1g \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /out /dirs/cache/home /dirs/cache/managed /dirs/cache/state /dirs/workspace \
    && for pkg in git libicu74 tzdata; do \
         dpkg -L "$pkg" | grep -Ev '^/usr/share/(doc|man|info|lintian)/' | while read -r f; do \
           if [ -d "$f" ] && [ ! -L "$f" ]; then mkdir -p "/out$f"; else cp -a --parents "$f" /out; fi; \
         done; \
       done \
    && ldd /usr/bin/git /usr/lib/git-core/git-remote-http | awk '/=>/ && $3 ~ /^\// {print $1, $3}' | sort -u \
       | grep -Ev '/(libc|libm|libgcc_s|libstdc\+\+|libpthread|libdl|librt|libresolv|libutil|libnsl|libanl|libssl|libcrypto)\.so' \
       | while read -r name path; do \
           real=$(realpath "$path"); mkdir -p "/out$(dirname "$real")"; cp -L "$real" "/out$real"; \
           [ "$name" = "$(basename "$real")" ] || ln -sfn "$(basename "$real")" "/out$(dirname "$real")/$name"; \
         done

# The base is the chiseled runtime-deps image of the same noble release: its digest is the multi-architecture index.
# Its user is `app` with uid 1654 (APP_UID), so the image runs as that number; /cache and /workspace belong to it.
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled@sha256:40eba5db69594d517c3a61e96bdacc2e3d6f8c99a44b52d55edee844d33518d0 AS base
COPY --from=clangd --chmod=a+rX /out/ /opt/tereditor/preinstalled/clangd/
COPY --from=native-deps /out/ /
COPY --from=native-deps --chown=1654:1654 /dirs/ /
ENV HOME=/cache/home \
    TEREDITOR_TOOLCHAINS=managed \
    TEREDITOR_MANAGED_CACHE=/cache/managed \
    TEREDITOR_STATE_DIR=/cache/state \
    TEREDITOR_ROOT=/workspace \
    DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    GIT_CONFIG_COUNT=1 \
    GIT_CONFIG_KEY_0=safe.directory \
    GIT_CONFIG_VALUE_0=*
USER 1654
WORKDIR /workspace
VOLUME /cache
ENTRYPOINT ["/opt/tereditor/tereditor-headless"]
CMD ["mcp"]

FROM base AS local
COPY --from=publish --chmod=a+rX /out/ /opt/tereditor/

FROM base AS prebuilt
ARG TARGETARCH
ARG PUBLISH_DIR=artifacts/headless/linux-${TARGETARCH}
COPY --chmod=a+rX ${PUBLISH_DIR}/ /opt/tereditor/

# Labels sit on the last stage, which is the default target. The local target is for development and carries none.
FROM prebuilt
ARG REVISION=unknown
ARG CREATED=unknown
LABEL org.opencontainers.image.title="tereditor-headless" \
      org.opencontainers.image.description="tereditor without a window: language, spec and Blueprint tools over MCP on stdio, for one workspace" \
      org.opencontainers.image.source="https://github.com/CommanderTvis/tereditor" \
      org.opencontainers.image.licenses="PolyForm-Shield-1.0.0" \
      org.opencontainers.image.revision="${REVISION}" \
      org.opencontainers.image.created="${CREATED}"
