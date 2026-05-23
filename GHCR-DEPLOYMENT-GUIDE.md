# GitHub → GHCR → Docker Deployment Guide

A reusable recipe for: **push to GitHub → GitHub Actions builds a Docker image → image lands in `ghcr.io` → server pulls and runs it**.

No third-party registry, no paid CI, no SSH-from-CI. The server is the only thing that talks to GHCR.

---

## What you get

- Every push to `main` produces a new image tagged `latest` (and with the commit SHA).
- Every `v*` git tag produces a semver-tagged image.
- The server pulls `latest` (or a pinned tag) and restarts the container.
- The whole pipeline is free for public repos. For private repos it uses your GitHub free-tier minutes + GHCR storage.

---

## Prerequisites

- A GitHub repo for the project.
- A server with Docker installed (`docker` + `docker compose` plugin).
- A working `Dockerfile` for the app.

---

## 1. The Dockerfile

Place a `Dockerfile` in the build context (usually the app folder). Use multi-stage builds — build with the SDK, run on the slim runtime image.

Sketch (adapt for your stack):

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["MyApp.csproj", "."]
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "MyApp.dll"]
```

The same pattern works for Node, Python, Go, etc. — only the base images and build steps change.

Tips:
- `EXPOSE` the port your app listens on inside the container (not the public port).
- Keep the runtime image small — only install OS packages you actually need at runtime (e.g. `ffmpeg`, `curl`).
- If your image needs config files or data, mount them as volumes from the host. Don't bake secrets in.

---

## 2. The GitHub Actions workflow

Create `.github/workflows/docker-publish.yml`:

```yaml
name: Build and Push Docker Image

on:
  push:
    branches: [main]
    tags: ['v*']
  workflow_dispatch:  # manual run from the Actions tab

env:
  REGISTRY: ghcr.io
  IMAGE_NAME: ${{ github.repository }}   # owner/repo, lowercased automatically

jobs:
  build-and-push:
    runs-on: ubuntu-latest
    permissions:
      contents: read
      packages: write                    # required to push to GHCR

    steps:
      - uses: actions/checkout@v4

      - name: Log in to GHCR
        uses: docker/login-action@v3
        with:
          registry: ${{ env.REGISTRY }}
          username: ${{ github.actor }}
          password: ${{ secrets.GITHUB_TOKEN }}

      - name: Extract tags & labels
        id: meta
        uses: docker/metadata-action@v5
        with:
          images: ${{ env.REGISTRY }}/${{ env.IMAGE_NAME }}
          tags: |
            type=raw,value=latest,enable={{is_default_branch}}
            type=semver,pattern={{version}}
            type=sha,prefix=

      - name: Build and push
        uses: docker/build-push-action@v5
        with:
          context: ./MyApp           # folder containing the Dockerfile
          push: true
          tags: ${{ steps.meta.outputs.tags }}
          labels: ${{ steps.meta.outputs.labels }}
```

Key points:
- `secrets.GITHUB_TOKEN` is provided automatically — you don't have to create anything.
- `permissions: packages: write` is what authorizes that token to push to GHCR.
- `context:` must point at the folder containing the Dockerfile.
- The `metadata-action` tag rules give you, for any push to `main`:
  - `latest`
  - `<short-sha>`
  - For `v1.2.3` git tags: `1.2.3`

---

## 3. First push & making the package public (optional)

Push to `main`. The workflow runs. When it finishes, the image is at:

```
ghcr.io/<owner>/<repo>:latest
```

By default the package is **private** and inherits its visibility from the repo. To make it public (so the server can pull without authentication):

1. GitHub → your profile → **Packages** → click the package.
2. **Package settings** → **Change visibility** → **Public**.

If you keep it private, the server will need to `docker login ghcr.io` with a Personal Access Token that has `read:packages` scope.

---

## 4. Running on the server

### Option A — `docker run` (recommended)

One-liner that pulls the latest image, stops + removes the old container, and starts a fresh one. Works the same on Linux and Windows (just adjust the host volume paths).

**Linux:**
```bash
docker pull ghcr.io/<owner>/<repo>:latest; \
docker stop myapp; \
docker rm myapp; \
docker run -d \
  --name myapp \
  --restart=unless-stopped \
  -p 5221:8080 \
  -v /srv/myapp/uploads:/app/wwwroot/uploads \
  -v /srv/myapp/config:/app/Data \
  -e ASPNETCORE_ENVIRONMENT=Production \
  ghcr.io/<owner>/<repo>:latest
```

**Windows (PowerShell or cmd):**
```bat
docker pull ghcr.io/urza/yap:latest & ^
docker stop yapdoc & ^
docker rm yapdoc & ^
docker run -d --name yapdoc ^
  --restart=unless-stopped ^
  -p 6777:8080 ^
  -v D:/dockerdata/yap-doc/uploads:/app/wwwroot/uploads ^
  -v D:/dockerdata/yap-doc/config:/app/Data ^
  ghcr.io/urza/yap:latest
```

Breakdown of the flags:
- `-d` — detached (runs in background).
- `--name myapp` — fixed container name so the next deploy can `docker stop`/`rm` it by name.
- `--restart=unless-stopped` — auto-start on boot / after a crash, but respect a manual `docker stop`.
- `-p HOST:CONTAINER` — publish the container port on the host. `CONTAINER` matches `EXPOSE` in the Dockerfile; `HOST` is whatever free port you want behind the reverse proxy.
- `-v HOST_PATH:CONTAINER_PATH` — mount a host folder into the container so data survives container replacement. Use absolute paths.
- `-e KEY=VALUE` — environment variables (e.g. `ASPNETCORE_ENVIRONMENT=Production`). Repeat the flag for each.
- The image tag is the **last** argument.

`docker stop` / `docker rm` will emit a harmless error on the very first run (no container to stop yet). Ignore it, or guard with `2>/dev/null || true` on Linux.

Tip: save the whole command as `~/deploy.sh` (Linux) or `deploy.bat` (Windows) so a redeploy is one keystroke.

### Option B — `docker compose`

If you prefer a declarative file, put this on the server as `docker-compose.yml`:

```yaml
services:
  app:
    image: ghcr.io/<owner>/<repo>:latest
    container_name: myapp
    ports:
      - "5221:8080"
    environment:
      - ASPNETCORE_ENVIRONMENT=Production
    volumes:
      - /srv/myapp/uploads:/app/wwwroot/uploads
      - /srv/myapp/config:/app/Data
    restart: unless-stopped
```

Deploy / update:
```bash
docker compose pull && docker compose up -d && docker image prune -f
```

### Pinning vs `latest`

`:latest` is convenient but means "whatever was last built". For production, pin to a SHA or semver tag (`:abc1234`, `:1.2.3`) so you control exactly when an update happens.

---

## 5. Private images: server-side login

If the GHCR package stays private:

1. On GitHub → **Settings → Developer settings → Personal access tokens → Tokens (classic)**.
2. Create a token with **`read:packages`** scope only.
3. On the server:

```bash
echo "<TOKEN>" | docker login ghcr.io -u <github-username> --password-stdin
```

Docker stores the credentials in `~/.docker/config.json` and reuses them for future pulls.

---

## 6. Reverse proxy (Caddy / Nginx)

The container exposes a plain HTTP port on the host (`5221` above). Put it behind a reverse proxy for TLS and a real hostname. Caddy example:

```
app.example.com {
    reverse_proxy localhost:5221
}
```

For apps with WebSockets / SignalR / long-polling (Blazor Server, etc.), make sure the proxy passes `Upgrade` / `Connection` headers — Caddy does this by default; Nginx needs explicit `proxy_set_header` directives.

---

## 7. One-line update on the server

Save your `docker run` command (from section 4) as a script — redeploy becomes a single command.

**Linux** (`~/deploy.sh`, `chmod +x` once):
```bash
#!/usr/bin/env bash
set -e
docker pull ghcr.io/<owner>/<repo>:latest
docker stop myapp 2>/dev/null || true
docker rm   myapp 2>/dev/null || true
docker run -d \
  --name myapp \
  --restart=unless-stopped \
  -p 5221:8080 \
  -v /srv/myapp/uploads:/app/wwwroot/uploads \
  -v /srv/myapp/config:/app/Data \
  -e ASPNETCORE_ENVIRONMENT=Production \
  ghcr.io/<owner>/<repo>:latest
docker image prune -f
```

**Windows** (`deploy.bat`):
```bat
@echo off
docker pull ghcr.io/urza/yap:latest
docker stop yapdoc
docker rm yapdoc
docker run -d --name yapdoc --restart=unless-stopped ^
  -p 6777:8080 ^
  -v D:/dockerdata/yap-doc/uploads:/app/wwwroot/uploads ^
  -v D:/dockerdata/yap-doc/config:/app/Data ^
  ghcr.io/urza/yap:latest
docker image prune -f
```

Run it manually after a push, or trigger from a webhook / scheduled task / `watchtower` if you want full automation.

---

## 8. Checklist for a new project

1. Add `Dockerfile` to the app folder.
2. Add `.github/workflows/docker-publish.yml` — change `context:` to your app folder.
3. Push to `main`, watch the Actions tab go green.
4. (Optional) Make the GHCR package public.
5. On the server: create a `docker-compose.yml` with `image: ghcr.io/<owner>/<repo>:latest` and any volumes / env vars / ports.
6. `docker compose up -d`.
7. Front it with a reverse proxy for TLS.

After that, the deploy loop is: **push → wait for the green check → `docker compose pull && up -d`**.

---

## Common gotchas

- **403 on push to GHCR**: missing `permissions: packages: write` in the workflow.
- **`denied` on server pull**: package is private and the server isn't logged in (`docker login ghcr.io`).
- **Image name must be lowercase** — GHCR rejects uppercase. `${{ github.repository }}` is already lowercased by GitHub.
- **Wrong `context:`** — build fails with "Dockerfile not found". The path is relative to the repo root.
- **Old container keeps running after `pull`** — `pull` only fetches the image. You must `up -d` (or `stop` + `run`) to actually restart with the new image.
- **Disk fills up over time** — old images accumulate. Run `docker image prune -f` after deploys, or schedule it.
- **Secrets in the image** — never `COPY` `appsettings.Production.json` or `.env` files containing secrets into the image. Mount them as volumes or pass via `environment:`.
