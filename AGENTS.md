# Fkh (Freddy's Kubernetes Helper) — Monorepo Guide

Fkh lets GitHub-authenticated users provision and manage **Business Central** containers on **Azure Kubernetes Service (AKS)**. An **Azure Functions** backend orchestrates Kubernetes; **Terraform** provisions Azure infrastructure. Clients share a **catalog-driven API** discovered at runtime.

## Repository layout

| Folder | Purpose |
|--------|---------|
| [`fkh-backend/`](fkh-backend/AGENTS.md) | Azure Functions API (orchestration, auth, catalog) |
| [`fkh-web/`](fkh-web/AGENTS.md) | React SPA for container management in the browser |
| [`fkh-cli/`](fkh-cli/AGENTS.md) | .NET global tool (`fkh`) — catalog proxy + local commands |
| [`fkh-vsix/`](fkh-vsix/AGENTS.md) | VS Code extension (tree views, catalog commands) |
| [`terraform/`](terraform/AGENTS.md) | Azure / AKS / Function App / optional Static Web App |
| [`ContainerScripts/`](ContainerScripts/AGENTS.md) | PowerShell setup scripts run inside BC containers |
| [`mssql-fts/`](mssql-fts/AGENTS.md) | SQL Server + Full-Text Search Docker image for AKS |
| [`Installation/`](Installation/AGENTS.md) | Step-by-step installation documentation |
| [`deployment-repo/`](deployment-repo/AGENTS.md) | Template for a **private** org deployment repository |
| [`.github/workflows/`](.github/workflows/) | Reusable CI/CD workflows (called from deployment repos) |

Root files: [`README.md`](README.md), [`SupportedClientVersions.json`](SupportedClientVersions.json), [`LICENSE`](LICENSE).

## Architecture (high level)

```
Clients (VSIX, CLI, Web)
    → GitHub PAT / OIDC
    → Azure Functions (fkh-backend)
        → AKS API (scale, logs, deployments)
        → ACR (images)
        → Blob storage (DB backups)
        → SQL on AKS (mssql-fts)
    ← Function catalog (GET /api/functions)
```

**Two-repo deployment model:** The public **Fkh** fork holds reusable workflows and source. Each organization uses a **private deployment repository** (from [`deployment-repo/`](deployment-repo/)) with `config/deployment.tfvars` and thin caller workflows that `workflow_call` into this repo.

## Cross-cutting patterns

### Function catalog

- Backend exposes metadata via `FunctionCatalog` and `GET /api/functions`.
- **CLI** and **VSIX** discover commands dynamically — do not hardcode endpoint lists in clients when the catalog can drive UI/CLI.
- New backend operations: add a `*Function.cs` trigger, a `Fkh*` service in `Services/`, and catalog entry.

### Protocol versioning

- Root [`SupportedClientVersions.json`](SupportedClientVersions.json) defines supported client protocol versions.
- Clients send `X-Fkh-Protocol-Version` and `X-Fkh-Client` on every request.
- When changing request/response contracts, bump protocol version and update all clients.

### Authentication & authorization

- GitHub **PAT** or **OIDC** (GitHub Actions) to the backend.
- Org **member**, **admin**, and **support** teams via env `ALLOWED_ORG_TEAMS` / `ADMIN_ORG_TEAMS` / `SUPPORT_ORG_TEAMS`.
- Optional explicit users via `ALLOWED_USERS` (username + role).
- Optional repo allowlists for OIDC (`allowed_oidc_repos` in tfvars).
- Never add long-lived secrets to source control; use GitHub Secrets and managed identities.

### ContainerScripts

- PowerShell scripts in [`ContainerScripts/`](ContainerScripts/) are linked into the backend build and served over HTTPS for BC container first-run setup.
- Changes affect live container provisioning — test via `createcontainer` or Create Images workflow.

## Build & deploy (summary)

| Component | Local build | CI / deploy |
|-----------|-------------|-------------|
| Backend | `dotnet build` in `fkh-backend/` | `DeployFkhFullStack.yml`, `UpdateFkhBackEnd.yml` |
| Web | `npm run build` in `fkh-web/` | Same full-stack / update workflows (SWA) |
| CLI | `dotnet pack` in `fkh-cli/` | `DeployFkhClients.yml` → NuGet |
| VSIX | `npm run build` + `vsce package` | `DeployFkhClients.yml` → Marketplace |
| Infra | `terraform apply` or `deploy.ps1` | `DeployFkhFullStack.yml` |
| MSSQL image | `docker build` in `mssql-fts/` | Full-stack workflow pushes to ACR |

There are **no automated unit test projects** in this repository. Validate via local dev, CI builds, and deployed environments.

## Coding standards (repo-wide)

- **C#:** .NET 8, `Nullable` and `ImplicitUsings` enabled. Namespace `Fkh`. Services prefixed `Fkh*`. Thin HTTP functions inherit `FunctionBase`.
- **TypeScript:** `strict` mode in `fkh-web` and `fkh-vsix`. No ESLint/Prettier at repo level — follow existing file style.
- **JSON API:** camelCase serialization on the backend.
- **Secrets:** Never commit passwords, private keys, or PATs. `deployment.tfvars` holds IDs and names only.
- **Scope:** Prefer minimal, focused changes. Match naming and patterns in the folder you touch.
- **License:** MIT with Commons Clause — no commercial resale/hosting of Fkh as a paid service without a separate license.

## When adding a feature

1. Implement backend service + HTTP function + catalog entry.
2. Update clients only if catalog metadata is insufficient (e.g. new UI in VSIX/web).
3. Update `SupportedClientVersions.json` if the protocol changes.
4. Document ops impact in `Installation/` if deploy steps change.
5. Extend Terraform only when new Azure/K8s resources are required.

## Per-folder documentation

Each project folder has its own `AGENTS.md` with stack details, commands, and folder-specific conventions:

- [fkh-backend/AGENTS.md](fkh-backend/AGENTS.md)
- [fkh-web/AGENTS.md](fkh-web/AGENTS.md)
- [fkh-cli/AGENTS.md](fkh-cli/AGENTS.md)
- [fkh-vsix/AGENTS.md](fkh-vsix/AGENTS.md)
- [terraform/AGENTS.md](terraform/AGENTS.md)
- [ContainerScripts/AGENTS.md](ContainerScripts/AGENTS.md)
- [mssql-fts/AGENTS.md](mssql-fts/AGENTS.md)
- [Installation/AGENTS.md](Installation/AGENTS.md)
- [deployment-repo/AGENTS.md](deployment-repo/AGENTS.md)
