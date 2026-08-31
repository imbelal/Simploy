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

**Next:** Auth, SOPS for secrets, real compose templating for multi-service apps, GH webhook auto-deploy.
