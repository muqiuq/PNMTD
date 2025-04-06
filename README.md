
![Logo](docs/3x/Artboard%201@3x.png)

# PNMTD - Personal Network Monitoring Tool

A monitoring application to keep an eye on Hosts, Emails, and DNS Zones. Useful for additional oversight of main monitoring systems.

![Build](https://github.com/muqiuq/PNMTD/actions/workflows/dotnet.yml/badge.svg) [![Docker](https://img.shields.io/docker/pulls/uisach/pnmtd?label=Docker&style=flat)](https://hub.docker.com/r/uisach/pnmtd/builds)

![Screenshot](docs/Screenshot.png)

# ⭐ Features
 - Monitoring state and uptime of remote systems
 - "Sensors" to monitoring using Ping, HTTP, HTTP-Heartbeat, E-Mail Heartbeat
 - DNS Zone monitoring
 - Notifications via E-Mail and Pushover
 - Custom intervals
 - Group Monitors (Sensors) in hosts
 - Frontend (Blazor): [PNMT.WebApp](https://github.com/muqiuq/PNMT.WebApp)

# 🧸 Motivation
 - I was using uptimerobot as oversight monitoring, but needed more features, especially the DNS Zone monitoring.
 - I needed to automatically process e-mails from secondary monitoring tools (like Synology NAS HyperBackup, Zabbix, ...).
 - Improve my C# Skills and learn Blazor ([PNMT.WebApp](https://github.com/muqiuq/PNMT.WebApp)).

## 🚧 Still In development

This project is currently in development and **not yet ready for production use**. If you are excited about what we're building and want to contribute, we warmly welcome anyone to **join our effort**! 

## 🔧 How to self-host

We only provide a Docker Compose setup here. It has only been tested on Debian 12.x.

### Requirements
 - Docker
 - Docker-Compose

### Steps

 - Download docker-compose.yml into /opt/pnmt (or your preferred folder)
```bash
mkdir -p /opt/pnmt && cd /opt/pnmt && wget https://raw.githubusercontent.com/muqiuq/PNMTD/refs/heads/master/docker-compose.yml
```
 - Adjust docker-compose
    - Change `Jwt__Issuer` to custom value (e.g., the domain where PNMT will be hosted)
    - Change `Jwt__Audience` to custom value (can be same value as Jwt_Issuer)
    - Change `Jwt__Key` to secure key (generate some random with `openssl rand -hex 32`)
      - e.g. set random value to `Jwt__Key` with a single command: `sed -i "/Jwt__Key=/s|^.*Jwt__Key=.*|      - Jwt__Key=$(openssl rand -hex 32)|" docker-compose.yml`
    - Optional: Set `externalApiurl` to external URL of pnmtd (e.g. https://pnmt.example.com)
    - Optional: 
      - Provide SMTP connection settings for E-Mail notification
      - Provide Pushover__ApiKey for Pushover notification
      - Provide IMAP connection settings for email receiving (Mailbox__)
      - Provide UplinkCheck configuration (requires SharedKey and custom remote endpoint e.g. a [DigitalOcean Function](https://www.digitalocean.com/products/functions) for example code see [function.py](examples/digitalocean_function.py))
 - Start containers
```bash
docker compose up 
```
 - Log in (http://yourhost:5000) with default credentials and change password
   - username: pnmt
   - password: pnmt

### Advanced setup
 - Set up a reverse proxy. It is recommended to use one domain for the backend and another for the frontend.
   - e.g. frontend: web.pnmt.example.com
   - e.g. backend: pnmt.example.com
 - Configure `Proxy`, `externalApiurl` and `externalDomain` in docker-compose.yml



