# nginx in front of PNMTD

nginx splits PNMT into two parts:

- **Public** (`pnmt.example.com`): only what sensors and agents call, which is the sensor API and the agents' paths. Every other path gets a 404.
- **Internal** (`pnmt.example.lan`): everything. That is the web UI under `/pnmt-admin/` and `/api/`, plus the sensor and agent paths.

PNMTD already requires sign-in on its own (deny by default). The split is a second layer on top: the UI, its login and the API are not offered on the public name at all.

The names, addresses and certificate paths below are examples:

| Example | Stands for |
|---|---|
| `pnmt.example.com` | the public name, used by sensors and agents |
| `pnmt.example.lan` | the internal name, used for the UI |
| `10.0.0.10:5001` | the PNMTD host and its published port (`PNMTD_PORT`) |
| `10.0.0.2` | nginx's internal address: the internal server listens on it, and PNMTD sees nginx's connections come from it |

## What is public

| Path | Methods | Used by |
|---|---|---|
| `/event/…` | GET, POST | sensors reporting a state (the write token is in the path) |
| `/read/…` | GET | anything that reads a sensor's last state (the read token is in the path) |
| `/agent/v1/join` | GET, POST | an agent joining with its one-time token (`xxxx-xxxx`) |
| `/agent/v1/ws` | GET, upgraded to a WebSocket | every joined agent, all the time, authenticated by the agent's key |
| `/agent/v1/download` | GET | the signed manifest that `pnmt-agent update` checks |
| `/agent/v1/download/<file>` | GET, HEAD | the agents' files: the Linux install line, `pnmt-agent update`, the download links in the UI |

These paths are anonymous by design. Each request authenticates with the token or key it carries. If nothing outside reads sensor states, you can leave `/read/` out.

**Not public:** `/pnmt-admin/` (the UI), `/api/` (the UI's API, including the login), `/mcp` (the MCP server, with `Mcp__Enabled=true`), `/healthz`, and every other path.

**Not through nginx at all:** UDP nodes (`5028/udp`) and the WireGuard gateway (`51820/udp`) don't use HTTP. Devices and routers send straight to the PNMTD host's published UDP ports, or through a port forward to them. PNMTD connects out to MikroTik routers over SSH, so routers need no path here either.

## The configuration

The configuration has two files. Both servers include the same header snippet, so their headers stay identical.

`/etc/nginx/snippets/pnmtd-proxy.conf`:

```nginx
# Included at server level. If a location sets even one proxy_set_header of its own, nginx drops
# all of these for that location. So set none inside the locations.
proxy_http_version 1.1;
proxy_set_header Host              $host;          # agents sign the address they use; PNMTD checks it against this
proxy_set_header X-Forwarded-Host  $host;          # set here, so a client can't send its own
proxy_set_header X-Forwarded-For   $remote_addr;   # nginx is the first hop: pass the address it sees
proxy_set_header X-Forwarded-Proto $scheme;
proxy_set_header Upgrade           $http_upgrade;  # the agents' WebSocket
proxy_set_header Connection        $connection_upgrade;
```

`/etc/nginx/conf.d/pnmtd.conf`:

```nginx
# Upgrade only when the client asked for it. Leave this map out if your nginx defines it already.
map $http_upgrade $connection_upgrade {
    default upgrade;
    ''      close;
}

upstream pnmtd {
    server 10.0.0.10:5001;
}

# -------------------------------------------------------------------------------------------------
# Public: the sensor API and the agents, nothing else.
# Plain HTTP stays plain HTTP. Sensors with http:// URLs must never be redirected: many don't follow
# a redirect, and a POST loses its body on one.
# -------------------------------------------------------------------------------------------------
server {
    listen 80;
    listen 443 ssl;
    # listen 5001;   # only if sensors still report to v0.12's http://pnmt.example.com:5001/…
    server_name pnmt.example.com;

    ssl_certificate     /etc/nginx/tls/pnmt.example.com.crt;
    ssl_certificate_key /etc/nginx/tls/pnmt.example.com.key;

    include snippets/pnmtd-proxy.conf;

    # This is nginx's default, set explicitly. PNMTD itself accepts up to 30 MB. A sensor that POSTs
    # a longer message gets a 413 from nginx, so raise this if your sensors send large messages.
    client_max_body_size 1m;

    # The sensor API, in any letter case: PNMTD accepts /EVENT/ too, and old sensors may use it.
    location ~* ^/(event|read)(/|$) {
        proxy_pass http://pnmtd;
    }

    # The agents (Windows and Linux). Exact paths: nothing else under /agent/ is passed on.
    location = /agent/v1/join {
        proxy_pass http://pnmtd;
    }
    location = /agent/v1/ws {
        proxy_pass http://pnmtd;
        # The agents ping every 30 s. These timeouts have to be longer than that.
        proxy_read_timeout 120s;
        proxy_send_timeout 120s;
    }
    location = /agent/v1/download {
        proxy_pass http://pnmtd;
    }
    location ~ ^/agent/v1/download/[^/]+$ {
        proxy_pass http://pnmtd;
        # If PNMTD doesn't have a file yet (e.g. after an update), the download waits while PNMTD
        # fetches it from GitHub, for up to 15 minutes.
        proxy_read_timeout 900s;
    }

    # Only needed for certbot's webroot method:
    # location ^~ /.well-known/acme-challenge/ { root /var/www/certbot; }

    location / {
        return 404;
    }
}

# -------------------------------------------------------------------------------------------------
# Internal: the UI, the API and everything else, on an address only the internal network reaches.
# -------------------------------------------------------------------------------------------------
server {
    listen 10.0.0.2:443 ssl;
    server_name pnmt.example.lan;

    ssl_certificate     /etc/nginx/tls/pnmt.example.lan.crt;
    ssl_certificate_key /etc/nginx/tls/pnmt.example.lan.key;

    include snippets/pnmtd-proxy.conf;

    # Firmware uploads for UDP nodes (Udp__Ota__MaxImageBytes, 16 MB by default).
    client_max_body_size 20m;

    location = / {
        return 302 /pnmt-admin/;
    }
    location / {
        proxy_pass http://pnmtd;   # nothing after the upstream: every path is passed on unchanged
        proxy_read_timeout 120s;   # covers an agent that was joined with the internal name
    }
    # The MCP server (docs/mcp-plan.md §11.4), with Mcp__Enabled=true. Its answers may stream (text/event-stream), and
    # Cookie's replies take up to Mcp__CookieWaitSeconds (60).
    location = /mcp {
        proxy_pass http://pnmtd;
        proxy_buffering off;
        proxy_read_timeout 120s;
    }
}

server {
    listen 10.0.0.2:80;
    server_name pnmt.example.lan;
    return 301 https://$host$request_uri;
}
```

Then run `nginx -t && systemctl reload nginx`.

You can also use one name for both. Set up split DNS and put the same `server_name` in both servers. nginx then tells them apart by the address a request arrives on, and `PublicBaseUrl` is that one name.

## PNMTD's settings

Set these in `.env` on the PNMTD host, then run `docker compose up -d`:

| Setting | Value | Why |
|---|---|---|
| `PublicBaseUrl` | `https://pnmt.example.com` | PNMT builds every address it hands out from this setting: the sensors' write and read URLs, the agents' install line and join command, and the download links. If it is empty, PNMT uses the browser's address instead. That is the internal name, which sensors can't reach. |
| `Proxy` | `::ffff:10.0.0.2` | Tells PNMTD to trust `X-Forwarded-For` and `X-Forwarded-Proto`, but only from nginx (one address, or `docker`). Without it, every request seems to come from nginx: the agents' per-address limits (10 joins a minute, 20 downloads an hour) count all servers together, the login rate limit and lockout count all users together, and events and the auth log show nginx's address. The internal UI also looks like plain HTTP to PNMTD, so it refuses MikroTik admin passwords and the backup key. |
| `PNMTD_BIND_ADDRESS` | `0.0.0.0` (the default), or the address nginx connects to | nginx runs on another host, so the port must be reachable from there. `127.0.0.1` only works when nginx runs on the PNMTD host itself; in that case use `Proxy=docker` and `server 127.0.0.1:5001`. |
| `Udp__PublicEndpoint` | `<address devices reach>:5028` | Only needed with UDP nodes. The UI tells devices where to send. By default that is `PublicBaseUrl`'s host, which is now nginx, and nginx carries no UDP. |
| `PNMTD_WG_EXTERNAL_HOST`, `PNMTD_WG_EXTERNAL_PORT` | the address and port routers dial | Only needed with WireGuard, for the same reason. |

## Why it is set up this way

- **Paths go through unchanged.** Don't strip or add a prefix. PNMT expects `/event/`, `/read/`, `/agent/`, `/api/` and `/pnmt-admin/` at the root of its host, and the agents resolve their paths against the root of the URL they joined with. When `proxy_pass http://pnmtd;` has nothing after the upstream, nginx passes the request URI exactly as the client sent it, including encoded message text.
- **No method filtering.** PNMTD answers a wrong method itself, with an empty 405 and `Allow`, just as v0.12 did. Sensor clients rely on the sensor API answering exactly as before.
- **The agents' paths are listed one by one.** If a later PNMT version adds a new agent path, add it here as well. Until then, nginx answers it with 404.
- **`/mcp` stays internal.** Each person's MCP token reaches PNMT with that person's rights; offer it only where the UI is. It has a location of its own because nginx must not buffer its streamed answers.
- **`/healthz` stays internal.** It answers only `ok` or `unavailable`, so an outside uptime monitor may use it. In that case, add `location = /healthz { proxy_pass http://pnmtd; }` to the public server.

## Checking it

From outside:

```sh
# The sensor API answers, with no redirect (use a test sensor: this records an event)
curl -i "http://pnmt.example.com/event/<write token>/200/proxy%20test"
curl -i "https://pnmt.example.com/read/<read token>/raw"

# The agents' paths
curl -s  https://pnmt.example.com/agent/v1/join                            # PNMT's version and key fingerprint
curl -sI https://pnmt.example.com/agent/v1/download/pnmt-agent-linux-x64   # 200 once PNMT has the file; a HEAD doesn't count toward the limit
curl -i --http1.1 --max-time 5 \
  -H 'Connection: Upgrade' -H 'Upgrade: websocket' \
  -H 'Sec-WebSocket-Version: 13' -H 'Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==' \
  -H 'Sec-WebSocket-Protocol: pnmt-agent.v1' \
  https://pnmt.example.com/agent/v1/ws                                     # 101 Switching Protocols, then curl times out

# Nothing else
curl -i https://pnmt.example.com/pnmt-admin/                               # 404
curl -i https://pnmt.example.com/api/v1/system/info                        # 404
curl -i -X POST https://pnmt.example.com/mcp                               # 404
```

In the UI, on `https://pnmt.example.lan/pnmt-admin/`:

- **Auth log:** your sign-in shows your workstation's address. If it shows nginx's address, `Proxy` is wrong. If it shows a `172.x` address, Docker's own proxy sits in between; use `Proxy=docker`.
- **Events:** the test event's source is the address of the machine that ran `curl`.
- **System health:** the public URL is `https://pnmt.example.com`.
- **Servers:** the agents show as connected.
