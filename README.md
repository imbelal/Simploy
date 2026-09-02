# Simploy - Dokploy/Coolify-like PaaS for Compose

Port of `BdShopManager/infra` Ansible automation to a generic control plane.

**Stack:** .NET 10 Api + React (Vite) + Agent (dotnet worker + Docker.DotNet) + Postgres (InMemory fallback)

**Core idea:** Multi-VM single project (staging VM + prod VM). Replaces `infra/inventory/group_vars/*` + 7 GitHub workflows.

| BdShopManager | Simploy |
|---|---|
| `infra/roles/app` deploy | `POST /api/deployments` -> Agent `:8089/deploy` |
| `infra/roles/rollout` canary 5→100 | Strategy=Canary, weighted Caddy (blue/green) |
| `provision.yml` base role | Agent install `curl .../api/servers/install \| bash` |
| `deploy-monitoring.yml` | Env toggle in UI |

## A deployment is: build from git, then run via compose

Create a **Project** with a `gitRepository` (+ optional `gitToken`, `dockerfilePath`,
`dockerContext`, registry creds). Link it to a **Server** via an **Environment**, then hit **Deploy**.

The control plane sends the Agent the full recipe. The Agent on the VM:

1. `git clone` the repo (`main` by default) into `/opt/simploy/<slug>/<slot>/src`
2. `docker build -f <dockerfile> -t <imageRepository>:<imageTag> <context>`
   (or `docker pull` if there's no Dockerfile — pure image deploy)
3. write `.env`, `docker-compose.yml` (repo's own, or a generated fallback), and a `Caddyfile`
4. `docker compose -p <slot> up -d`
5. health-gate on `<service>:<port>/health`

Images without a `gitRepository` still work (pull-only). Canary is blue/green: the Agent keeps
the previous image as `<service>-old` and renders a weighted Caddyfile between new/old.

## Install the Agent on a VM (one-liner)

```bash
# on the target VM (Ubuntu/Debian + Docker)
curl -fsSL https://<control-plane>/api/servers/install | SIMPLOY_REPO=https://github.com/<you>/Simploy bash
```

or from source:

```bash
SIMPLOY_REPO=/path/to/Simploy bash scripts/install.sh
```

It installs Docker if missing, builds `simploy-agent`, and runs it with `--network host` +
the host docker socket. Then add the VM's IP as a Server in the UI -> it reports **Online**.

## Run locally (InMemory DB, no postgres needed)
```bash
cd src/Simploy.Api && dotnet run  # :5000
cd web && npm run dev              # :5173
```
Set `UseInMemoryDb=false` + `docker compose up` for postgres.

## Compose / API endpoints
- `POST /api/deployments` `{ environmentId, imageTag, strategy, commitSha, canaryPercent }` -> triggers deploy
- `GET  /api/deployments/:id` -> status/logs
- `POST /api/deployments/:id/rollback`
- `GET  /api/servers/install` -> the agent installer script
- `POST /api/servers/:id/check` -> ping agent :8089/health

## Authentication
- **Control plane** (API/UI) requires login. A single admin account is configured via env
  (`Auth__AdminUser`, `Auth__AdminPassword`, `Auth__JwtSecret`). The UI shows a login page;
  the API returns a JWT and all `/api/*` endpoints are `[Authorize]` (except `/health` and
  `/api/servers/install`).
- **Agent** (`:8089`) requires a shared bearer token (`Agent__Token`). It must match on the
  API (`Agent__Token`) and the Agent. `/health` stays public so the UI can check servers.

Set these (esp. on a public server): `SIMPLOY_ADMIN_USER`, `SIMPLOY_ADMIN_PASSWORD`,
`SIMPLOY_JWT_SECRET` (≥32 chars), and `SIMPLOY_AGENT_TOKEN` — must be identical on both the
API and the Agent.

### GitHub App (recommended over PATs for private repos)
Replace long-lived PATs with a GitHub App that issues short-lived, repo-scoped tokens automatically.

1. Register a **GitHub App** (GitHub → Settings → Developer settings → GitHub Apps → New GitHub App):
   - Set the **Setup URL** to `https://<your-simploy>/api/github/callback`.
   - Under **Repository permissions** grant **Contents: Read-only**.
   - Generate a **Private Key** and download the `.pem`.
2. In Simploy's env (or `.env`) set:
   ```bash
   SIMPLOY_GH_APP_ID=<app id>
   SIMPLOY_GH_CLIENT_ID=<client id>
   SIMPLOY_GH_CLIENT_SECRET=<client secret>
   SIMPLOY_GH_SLUG=<app-slug>          # from the app's URL, e.g. my-simploy
   SIMPLOY_GH_PRIVATE_KEY="$(cat app.pem | tr '\n' ' ')"   # PEM (newlines escaped)
   ```
3. In the UI → **Projects** → **Install GitHub App** → authorize + choose the repos.
4. **Bind installation** → pick the installation in the project.

On deploy, Simploy mints a short-lived installation token and clones with it. No PAT needed;
leave the project's Git token blank. (We recommend moving the private key to an encrypted
secret store — see Next.)

## Managed databases
Create databases from the **Databases** tab: Postgres / MySQL / Redis / MongoDB. Simploy
provisions each as a container on your server with a persistent volume, on the `simploy-proxy`
network, and auto-generates a password. Apps on the same proxy network connect by host name:

```
Host=db-<name>  Port=<port>  Database=<name>  User=<user>  Password=<generated>
```

You can still ship your own DB in an app's `docker-compose.yml` too (Option A) — both coexist.
Set the password via the app's env vars if you want to override the generated one.

## Backups
Back up Simploy's own control-plane database from the **Backups** tab: enable scheduled backups
(interval + retention), or click "Back up now". The agent `pg_dump`s the control-plane Postgres
into `/opt/simploy/backups/`, pruned by retention. Files live on the VM (keep them off the box or
sync elsewhere for true redundancy). **Never run `docker compose down -v`** — that removes the
`pgdata` volume. Always back up before unsafe operations.

**Next:** SOPS for secrets, real compose templating for multi-service apps, GH webhook auto-deploy.
