# PNMTD's image (docs/webui-migration-plan.md §6.3).
#
# Both build stages run on the build platform: the web UI is plain files, and the SDK stage
# publishes one portable, framework-dependent build that runs on amd64 and arm64 alike, restored
# in locked mode from the committed lock files. A multi-arch image compiles nothing under emulation.

# --- 1. the web UI (PNMTD.WebUI → dist/, served under /pnmt-admin) -------------------------------
# Node runs here, or in the containers of start.sh, run-tests.sh and tools/npm.sh: never on a host.
# Node 24 (LTS) on Debian rather than Alpine: musl's resolver fails a whole lookup when Podman's DNS
# answers AAAA with SERVFAIL. npm ci installs exactly the lock file, without install scripts.
FROM --platform=$BUILDPLATFORM docker.io/library/node:24-slim AS web
WORKDIR /web
ENV NPM_CONFIG_UPDATE_NOTIFIER=false NPM_CONFIG_FUND=false NPM_CONFIG_AUDIT=false
COPY PNMTD.WebUI/package.json PNMTD.WebUI/package-lock.json ./
RUN npm ci --ignore-scripts --loglevel=error
COPY PNMTD.WebUI/ ./
RUN npm run build

# --- 2. the .NET app ---------------------------------------------------------------------------
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
# The projects and their lock files first, restored in a layer of their own: a change to the code
# alone reuses it.
COPY PNMTD/PNMTD.csproj PNMTD/packages.lock.json PNMTD/
COPY PNMTD.Lib/PNMTD.Lib.csproj PNMTD.Lib/packages.lock.json PNMTD.Lib/
RUN dotnet restore PNMTD/PNMTD.csproj --locked-mode
COPY PNMTD/ PNMTD/
COPY PNMTD.Lib/ PNMTD.Lib/
COPY --from=web /web/dist PNMTD/wwwroot/pnmt-admin/
RUN dotnet publish PNMTD/PNMTD.csproj -c Release -o /app --no-restore -p:UseAppHost=false \
 && rm -f /app/*.pdb

# --- 3. the runtime ------------------------------------------------------------------------------
# Ubuntu, as Microsoft's default .NET 10 image: glibc's resolver copes with Podman's DNS, which
# musl's does not (an AAAA SERVFAIL fails the whole lookup), and ICU keeps culture behaviour as
# v0.12.1 had it on Debian. tzdata is in the image already: TZ names the zone for local-day rules.
FROM mcr.microsoft.com/dotnet/aspnet:10.0
LABEL org.opencontainers.image.title="PNMTD" \
      org.opencontainers.image.description="PNMT monitoring daemon: sensor API, monitoring tasks and the admin UI." \
      org.opencontainers.image.source="https://github.com/muqiuq/PNMTD"
# wget for the health check. iputils-ping for PING sensors: without CAP_NET_RAW, .NET's Ping runs
# the system's ping, which uses an unprivileged ICMP socket where net.ipv4.ping_group_range allows
# it (the compose file sets it; Docker's default allows it too). /var/lib/pnmtd belongs to the
# image's non-root user, who runs the app. `pnmtd …` is the app's command line. libgssapi-krb5-2:
# Npgsql probes for it at every start and complains loudly when it is missing. wireguard-tools,
# iproute2 and nftables: the WireGuard gateway, the same image as the service pnmtd-wg, run as root
# with NET_ADMIN only (docs/mikrotik-plan.md §3.4); the app itself never uses them.
RUN apt-get update \
 && apt-get install -y --no-install-recommends wget iputils-ping libgssapi-krb5-2 wireguard-tools iproute2 nftables \
 && rm -rf /var/lib/apt/lists/* \
 # ping's file capability (cap_net_raw) makes exec fail outright under cap_drop ALL and
 # no-new-privileges; a plain copy of the binary drops it and leaves the ICMP socket path.
 && cp /usr/bin/ping /usr/bin/ping.plain && mv -f /usr/bin/ping.plain /usr/bin/ping \
 && mkdir -p /var/lib/pnmtd/keys /var/lib/pnmtd/downloads \
 && chown -R app:app /var/lib/pnmtd \
 && printf '#!/bin/sh\nexec dotnet /app/PNMTD.dll "$@"\n' > /usr/local/bin/pnmtd \
 && chmod 0755 /usr/local/bin/pnmtd
COPY --from=build /app /app
USER app
WORKDIR /app
ENV DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1
EXPOSE 8080
# UDP nodes (Udp__Port, default 5028): heartbeats, data, commands and firmware updates.
EXPOSE 5028/udp
# The WireGuard gateway (pnmtd-wg): routers and custom tunnels.
EXPOSE 51820/udp
# 200 while the database answers. The compose file repeats it: Podman's OCI images drop HEALTHCHECK.
HEALTHCHECK --interval=30s --timeout=5s --start-period=60s --retries=3 \
  CMD wget -q -O /dev/null "http://127.0.0.1:${ASPNETCORE_HTTP_PORTS:-8080}/healthz" || exit 1
ENTRYPOINT ["dotnet", "/app/PNMTD.dll"]
