# mssql-fts

Minimal **Docker** image: Microsoft SQL Server (2022 or 2025) with **Full-Text Search (FTS)** enabled. Used as the persisted database server for Business Central containers on AKS.

## Tech stack

- **Docker**
- Base: `mcr.microsoft.com/mssql/server:<tag>`, selected by `sql_version` in tfvars (`2022`, `2025` or a specific tag such as `2025-CU9-ubuntu-24.04`)
- Adds `mssql-server-fts` during image build from the repo matching the SQL major version and the base image's Ubuntu version (read from `/etc/os-release`)

Files: `Dockerfile` (parameterized via build args), `Build-MssqlImage.ps1` (resolves `sql_version`, builds, pushes).

## Build commands

### CI (full stack deploy) and `terraform/deploy.ps1`

```powershell
$acrName = terraform output -raw acr_name
$acrLoginServer = terraform output -raw acr_login_server
az acr login --name $acrName
../mssql-fts/Build-MssqlImage.ps1 -VarFile <tfvars> -AcrLoginServer $acrLoginServer
```

The image is tagged with the base tag (e.g. `2022-latest`, `2025-CU9-ubuntu-24.04`). The SQL pod uses `image_pull_policy = "Always"`, so a rebuilt image is picked up on the next pod start (e.g. node image upgrade), not on deploy.

### Local

```powershell
cd mssql-fts
docker build -t mssql-server-fts:2022-latest .
```

## Test commands

No automated tests. Verify SQL + FTS after deploy by creating a BC container that uses the in-cluster SQL instance (via Fkh create container flow).

## Architecture

- Image is pushed to the deployment's **ACR** during **Deploy Full Stack**.
- **Terraform** (`kubernetes.tf`) deploys SQL using this image on the AKS cluster.
- SA password comes from GitHub Secret `SQL_SA_PASSWORD` (never in git).

## Conventions

- Keep the Dockerfile minimal — only add packages required for BC (FTS).
- Changing `sql_version` from 2022 to 2025 upgrades all databases on the persistent disk on first start and cannot be reverted.
- Do not embed passwords in the Dockerfile; use K8s secrets from Terraform.

## Related

- [terraform/AGENTS.md](../terraform/AGENTS.md) — SQL workload on AKS
- [.github/workflows/DeployFkhFullStack.yml](../.github/workflows/DeployFkhFullStack.yml)
