# mssql-fts

Minimal **Docker** image: Microsoft SQL Server 2022 with **Full-Text Search (FTS)** enabled. Used as the persisted database server for Business Central containers on AKS.

## Tech stack

- **Docker**
- Base: `mcr.microsoft.com/mssql/server:2022-latest`
- Adds `mssql-server-fts` during image build

Single file: `Dockerfile`.

## Build commands

### CI (full stack deploy)

From repo workflow after Terraform outputs ACR login server:

```powershell
$acrLoginServer = terraform output -raw acr_login_server
docker build -t "$acrLoginServer/mssql-server-fts:2022-latest" ../mssql-fts
docker push "$acrLoginServer/mssql-server-fts:2022-latest"
```

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
- Tag remains `2022-latest` unless you coordinate a migration across Terraform and existing clusters.
- Do not embed passwords in the Dockerfile; use K8s secrets from Terraform.

## Related

- [terraform/AGENTS.md](../terraform/AGENTS.md) — SQL workload on AKS
- [.github/workflows/DeployFkhFullStack.yml](../.github/workflows/DeployFkhFullStack.yml)
