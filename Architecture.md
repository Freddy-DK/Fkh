# Fkh Architecture

Fkh is a **GitHub-authenticated AKS container provisioner** that allows authorized GitHub team members to create on-demand Business Central environments on Azure Kubernetes Service — directly from VS Code or a CLI — without requiring Azure credentials.

## High-Level Overview

```mermaid
graph LR
    subgraph Clients
        VSIX["VS Code Extension"]
        CLI["CLI Tool"]
    end

    GA["GitHub Actions<br/>(CreateImages)"]
    GA2["GitHub Actions"]
    USER["User"]
    FUNC["Fkh Backend<br/>(Azure Function)"]
    GH["GitHub API"]
    GHTEAMS["GitHub<br/>Org Teams"]

    subgraph AKS["AKS Cluster"]
        direction TB
        SQL["SQL Server<br/>(Linux)"]
        BC["BC Containers<br/>(Windows)"]
        SPOT["BC Containers<br/>(Windows Spot)"]
    end

    ACR["Container<br/>Registry"]
    BLOB["Blob Storage<br/>(.bak files)"]
    AAD["Azure AD<br/>Deploy Identity"]

    USER --> VSIX
    USER --> CLI
    USER -->|"team membership"| GHTEAMS
    VSIX -->|"GitHub token"| FUNC
    CLI -->|"GitHub token<br/>or OIDC token"| FUNC
    FUNC -->|"validate user"| GH
    FUNC <-->|"check membership"| GHTEAMS
    FUNC -->|"manage containers"| AKS
    FUNC -->|"check/pull images"| ACR
    FUNC -->|"database backups"| BLOB
    FUNC -->|"dispatch workflow<br/>(GitHub App)"| GA
    BC & SPOT -->|"TCP 1433"| SQL
    GA -->|"OIDC"| AAD
    GA2 -->|"OIDC token"| CLI
    AAD -->|"federated credential"| ACR
    AAD -->|"federated credential"| BLOB
```

## Request Flow

```mermaid
graph LR
    A["User"] -->|"POST /api/CreateContainer<br/>Bearer: github_token"| B["FunctionBase<br/>(auth + validate)"]
    B -->|"GET /user"| C["GitHub API"]
    B --> D["FkhCreateContainer"]
    D --> E["Check ACR image"]
    D --> F["Restore DB"]
    D --> G["Create K8s resources"]
    E -->|"missing?"| H["Trigger GitHub Actions<br/>(build image)"]
```

## Infrastructure Layout

```mermaid
graph TB
    subgraph RG["Resource Group: fkh-&lt;org&gt;"]
        subgraph AKS["AKS Cluster"]
            LP["Linux Pool<br/>(SQL Server, persisted)"]
            WP["Windows Pool<br/>(BC containers)"]
            SP["Windows Spot Pool<br/>(optional, cheaper)"]
        end
        FA["Function App<br/>(Consumption)"]
        ACR["Container Registry"]
        MI["Managed Identity"]
        DBS["Storage: DBS<br/>(database backups)"]
        FST["Storage: Func<br/>(runtime state)"]
        MON["Log Analytics<br/>+ App Insights"]
    end

    GH["GitHub<br/>(Teams + App)"]

    FA -->|"Managed Identity"| AKS & ACR & DBS
    MI -.-> FA
    GH -.->|"auth"| FA
```

## Component Descriptions

### User Interfaces

| Component | Path | Description |
|-----------|------|-------------|
| **VS Code Extension** | `fkh-vsix/` | Registers commands to create/remove containers. Uses VS Code's built-in GitHub auth to obtain a Bearer token and calls the Function App API. Fetches the function catalog for dynamic parameter prompts. Auto-detects public IP for SQL access. Parameter defaults can be set via `fkh.<Function>.<param>` settings. |
| **CLI Tool** | `fkh-cli/` | Standalone .NET executable (`fkh.exe`). Authenticates via `--useOIDC` (GitHub Actions OIDC), `GH_TOKEN`, `GITHUB_TOKEN`, or `gh auth token`. Interactively prompts for parameters with masked password input. Auto-detects public IP for SQL access. |

### Azure Functions Backend

| Component | Path | Description |
|-----------|------|-------------|
| **FunctionBase** | `fkh-backend/FunctionBase.cs` | Base class for all HTTP functions. Extracts Bearer token, validates it against GitHub API (PAT) or GitHub Actions OIDC (JWT), checks team membership, parses and validates parameters against the function catalog, and injects the GitHub username. Includes brute-force protection (IP blocking after 3 failed attempts within 5 minutes). |
| **GitHubAuthService** | `fkh-backend/Services/GitHubAuthService.cs` | Calls `GET /user` and `GET /orgs/{org}/teams/{team}/memberships/{username}` to authenticate and authorize requests. Allowed org/team pairs loaded from `ALLOWED_ORG_TEAMS`, `ADMIN_ORG_TEAMS`, and `SUPPORT_ORG_TEAMS` env vars. Individual user grants loaded from `ALLOWED_USERS`. |
| **GitHubOidcService** | `fkh-backend/Services/GitHubOidcService.cs` | Validates GitHub Actions OIDC JWTs against GitHub's OpenID Connect discovery endpoint. Checks the `repository` claim against the `ALLOWED_OIDC_REPOS` allow-list. OIDC callers are granted admin privileges. |
| **GitHubAppTokenService** | `fkh-backend/Services/GitHubAppTokenService.cs` | Creates JWTs signed with the GitHub App private key, exchanges for installation access tokens, and dispatches the `createImages` workflow when an image is missing from ACR. |
| **FkhCreateContainer** | `fkh-backend/Services/FkhCreateContainer.cs` | Orchestrates container creation: ACR image check → database backup SAS URL → k8s exec to download and restore database → create K8s deployment, service, and secret. |
| **FkhRemoveContainer** | `fkh-backend/Services/FkhRemoveContainer.cs` | Removes Kubernetes resources (deployment, service, secret) and drops the database for a given container. |
| **FkhScaleContainer** | `fkh-backend/Services/FkhScaleContainer.cs` | Scales a container's deployment: StopContainer sets replicas to 0, StartContainer sets replicas to 1. Database is preserved across stop/start. |
| **FkhListVMs** | `fkh-backend/Services/FkhListVMs.cs` | Lists VMs filtered by user (or all). Shows status, image, web client URL, and CPU/memory usage via the metrics API. |
| **FkhAllowSqlAccess** | `fkh-backend/Services/FkhAllowSqlAccess.cs` | Manages temporary external SQL Server access. Creates a per-user LoadBalancer service (IP-restricted via `loadBalancerSourceRanges`) and a NetworkPolicy allowing the user's IP through to the MSSQL pod. Auto-revokes expired grants via the timer-triggered AutoStop function. |
| **FkhServiceBase** | `fkh-backend/Services/FkhServiceBase.cs` | Shared base class with AKS/ACR/Storage config, Kubernetes client creation via managed identity, and k8s exec helpers (`FindMssqlPodAsync`, `ExecInMssqlPodAsync`). |

### Infrastructure (Terraform)

| Resource | File | Description |
|----------|------|-------------|
| **AKS Cluster** | `main.tf` | Linux system pool (1× D4s_v5) + Windows autoscale pool (0–10 nodes). Azure CNI overlay networking. v6 series is not yet supported. |
| **Function App** | `function.tf` | Windows Consumption (Y1) plan. Isolated .NET 8 worker. All config injected via app settings. |
| **SQL Server** | `kubernetes.tf` | `mssql/server:2022-latest` on Linux pod with 128 Gi Premium SSD PVC. ClusterIP service on port 1433. Network policy restricts ingress to `app-type: windows-servicetier` pods. External access can be temporarily granted per-user via `AllowSqlAccess`. |
| **ACR** | `acr.tf` | Basic SKU. AKS kubelet identity gets `AcrPull`; GitHub Actions federated identity gets `AcrPush`. |
| **Managed Identity** | `identity.tf` | User-assigned identity with AKS Contributor + Storage Blob Data Contributor roles. Federated credential for GitHub Actions OIDC. |
| **Storage (DBS)** | `function.tf` | `fkh{org}dbs` — holds database backup blobs in a `cronus` container, keyed by image tag. |
| **Storage (Func)** | `function.tf` | `fkh{org}func` — Azure Functions runtime state (queues, tables). |
| **GitHub Team** | `github.tf` | Manages the authorized team within the GitHub organization. |
| **Monitoring** | `monitoring.tf` | Log Analytics workspace (30-day retention) + Application Insights for function telemetry. |

### GitHub Actions

| Workflow | Trigger | Description |
|----------|---------|-------------|
| **CreateImages** | `workflow_dispatch` (artifactUrls) | Downloads BC artifacts via `BcContainerHelper`, extracts and uploads the `.bak` database backup to blob storage, builds the container image with `New-BcImage`, and pushes to ACR. Authenticates to Azure via OIDC federated identity. |

## Authentication Flow

There are three authentication paths:

1. **User requests** — VS Code / CLI sends a GitHub PAT to the Function App, which validates it against GitHub API.
2. **GitHub Actions → Function App (OIDC)** — A GitHub Actions workflow authenticates to the Function App using an OIDC ID token. The Function App validates the token and checks the repository against the `ALLOWED_OIDC_REPOS` allow-list.
3. **GitHub Actions → Azure (OIDC)** — GitHub Actions authenticates directly to Azure AD via a federated credential to push images to ACR and upload database backups to Blob Storage.

```mermaid
graph LR
    subgraph "Auth"
        U["User<br/>(VS Code / CLI)"] -->|"GitHub PAT"| FB["FunctionBase"]
        GA_OIDC["GitHub Actions<br/>(OIDC ID token)"] -->|"Bearer: id_token"| FB
        FB -->|"GET /user"| GH["GitHub API"]
        FB -->|"GET /teams/.../memberships"| GH
        GH -->|"username + team ✓"| FB
    end

    subgraph "Function → Azure"
        FB -->|"Managed Identity"| AKS["AKS<br/>(k8s API)"]
        FB -->|"Managed Identity"| ACR["Container<br/>Registry"]
        FB -->|"Managed Identity"| BLOB["Blob<br/>Storage"]
    end

    subgraph "CI/CD → Azure (direct)"
        GA["GitHub Actions"] -->|"OIDC token"| AAD["Azure AD"]
        AAD -->|"federated credential"| ACR2["ACR<br/>(AcrPush)"]
        AAD -->|"federated credential"| BLOB2["Blob Storage<br/>(upload .bak)"]
    end
```

### Detailed Sequence

```mermaid
sequenceDiagram
    participant User as User (VS Code / CLI)
    participant Func as Azure Function
    participant GH as GitHub API
    participant AKS as AKS (k8s API)
    participant Blob as Blob Storage
    participant ACR as ACR

    User->>Func: POST /api/CreateContainer (Bearer: gh_token)
    Func->>GH: GET /user (validate token)
    GH-->>Func: { login: "username" }
    Func->>GH: GET /orgs/{org}/teams/{team}/memberships/{username}
    GH-->>Func: { state: "active" }
    Note over Func: Authorized ✓

    Func->>ACR: Check image exists (ManagedIdentityCredential)
    ACR-->>Func: 200 OK / 404

    Func->>Blob: GetUserDelegationKey + build SAS URL (ManagedIdentity)
    Blob-->>Func: SAS URL

    Func->>AKS: Get kubeconfig (ManagedIdentity → ARM)
    Func->>AKS: k8s exec → curl .bak into mssql pod
    Func->>AKS: k8s exec → sqlcmd RESTORE DATABASE
    Func->>AKS: Create Deployment + Service + Secret

    Func-->>User: Container created (FQDN, deployment info)
```

## Container Creation Flow

1. **Image check** — Verify the requested BC image exists in ACR. If missing, trigger the `createImages` GitHub Actions workflow via the GitHub App and return an error asking the user to retry.
2. **Database backup** — Generate a 1-hour read-only SAS URL for the `.bak` blob in the DBS storage account.
3. **Database existence check** — K8s exec into the mssql pod and run `sqlcmd` to verify the database doesn't already exist.
4. **Database restore** — K8s exec to `curl` the backup into the pod, then `sqlcmd` to `RESTORE DATABASE` with `MOVE` clauses for data/log files.
5. **Kubernetes resources** — Create a Secret (admin password), a Deployment (Windows container with BC image and database env vars), and a LoadBalancer Service (public IP with Azure DNS label).
6. **Return** — FQDN (`{appName}.{region}.cloudapp.azure.com`), deployment name, and database name.

## SQL Access Flow

`AllowSqlAccess` grants temporary direct SQL Server access from a user's public IP:

1. **Create LoadBalancer service** — `mssql-ext-{username}` with `loadBalancerSourceRanges` set to the user's IP/32. Targets the mssql pod on port 1433.
2. **Create NetworkPolicy** — `mssql-allow-ip-{username}` with an ingress rule allowing the user's CIDR to reach the mssql pod on port 1433.
3. **Wait for external IP** — Polls the service status until Azure assigns a public IP (up to ~2.5 minutes).
4. **Return** — SQL endpoint (`{externalIp},1433`), allowed IP, and auto-revoke time.

Access is auto-revoked by the `AutoStop` timer function (runs every 30 minutes),
which checks for `fkh/sql-access-revoke-at` annotations on the services and
deletes expired resources. Users can also revoke access immediately via `RevokeSqlAccess`.

Each user can have only one active SQL access grant. Calling `AllowSqlAccess` again
replaces the existing grant (updating the allowed IP and extending the timer).

## Kubernetes Cluster

```mermaid
graph TB
    subgraph AKS["AKS Cluster (Azure CNI overlay)"]
        subgraph LP["Linux Pool (1 node, fixed)"]
            MSSQL["mssql-deployment<br/>(SQL Server 2022)"]
            PVC["mssql-data-pvc<br/>(Premium SSD)"]
            MSSQL --- PVC
        end

        SVC_MSSQL["mssql-service<br/>(ClusterIP :1433)"]
        NP["NetworkPolicy<br/>(app-type: windows-servicetier only)"]
        SVC_MSSQL --> MSSQL
        NP -.->|"restrict ingress"| MSSQL

        subgraph WP["Windows Pool (autoscaler 0–N)"]
            BC1["bc-container-1<br/>replicas: 1 (running)"]
            BC2["bc-container-2<br/>replicas: 0 (stopped)"]
            PP["fkh-image-prepull<br/>(DaemonSet, optional)"]
            OP["fkh-overprovision<br/>(placeholder, optional)"]
        end

        subgraph SP["Spot Pool (optional, autoscaler 0–N)"]
            BC3["bc-container-3<br/>replicas: 1 (spot)"]
        end

        BC1 -->|"TCP 1433"| SVC_MSSQL
        BC3 -->|"TCP 1433"| SVC_MSSQL
    end

    SVC1["LoadBalancer<br/>{app}.{region}.cloudapp.azure.com"]
    SVC1 --> BC1

    subgraph "Stop / Start"
        STOP["StopContainer<br/>replicas: 1 → 0"]
        START["StartContainer<br/>replicas: 0 → 1"]
    end

    STOP -.-> BC2
    START -.-> BC2

    subgraph "Temporary SQL Access (external)"
        EXT_USER["User's IP"]
        EXT_SVC["mssql-ext-{user}<br/>(LoadBalancer, IP-restricted)"]
        EXT_NP["mssql-allow-ip-{user}<br/>(NetworkPolicy)"]
    end

    EXT_USER --> EXT_SVC
    EXT_SVC -->|":1433"| MSSQL
    EXT_NP -.->|"allow user IP"| MSSQL
```

### Node Pools

The AKS cluster has up to three node pools:

| Pool | OS | VM Size | Scaling | Purpose |
|------|----|---------|---------|---------|
| **linuxpool** | Ubuntu | Configurable (`linux_vm_size`) | Fixed, 1 node | Runs the SQL Server pod. Always on. |
| **win** | Windows 2022 | Configurable (`windows_vm_size`) | Autoscaler, `windows_min_node_count`–`windows_max_node_count` | Runs BC containers. Can scale to zero when idle. |
| **spot** (optional) | Windows 2022 | Configurable (`windows_spot_vm_size`) | Autoscaler, `windows_spot_min_node_count`–`windows_spot_max_node_count` | Cheaper spot-priced Windows nodes. Can be evicted by Azure at any time (eviction policy: Delete). Tainted with `kubernetes.azure.com/scalesetpriority=spot:NoSchedule` so only containers that explicitly tolerate spot scheduling land here. |

Networking uses Azure CNI overlay.

### BC Containers (Windows)

Each BC container is a Kubernetes Deployment + LoadBalancer Service + Secret, created dynamically by `FkhCreateContainer`. The deployment runs a single Windows pod with the BC image from ACR. The service exposes the web client on a public IP with an Azure DNS label (`{appName}.{region}.cloudapp.azure.com`).

**Stop / Start** — Containers are stopped and started by scaling the deployment's replica count:

- **StopContainer** — Sets `replicas: 0`. The pod is terminated but the Deployment, Service, and Secret remain. The database is preserved in SQL Server.
- **StartContainer** — Sets `replicas: 1`. A new pod is created from the same image, reconnects to the existing database, and the same public DNS name resolves again once the LoadBalancer is ready.

This means stopping a container releases all Windows node resources (and the autoscaler can scale down the node pool), while the database and Kubernetes metadata survive for a fast restart.

**Auto-stop** — An optional `fkh/auto-stop-at` annotation on the deployment records a UTC timestamp. The `AutoStop` timer function (runs every 30 minutes) scales down any deployment past its stop time. Users can set, extend, or clear auto-stop via the API.

### Overprovisioning (optional)

When `windows_overprovision = true`, a low-priority placeholder deployment (`fkh-overprovision`) keeps a pause container running on a Windows node, reserving 500m CPU + 3 GiB. When a real BC container is scheduled, it preempts the placeholder and starts immediately. The displaced placeholder triggers the autoscaler to provision a new VM in the background, restoring spare capacity.

### Image Pre-Pull (optional)

When `windows_prepull_images` is set, a DaemonSet (`fkh-image-prepull`) runs init containers on every Windows node (including spot nodes) to pull the listed images. This avoids the ~14-minute image pull when creating the first container on a fresh node.

### SQL Server (Linux)

SQL Server 2022 runs as a single-replica Deployment on the Linux node pool (`node_selector: kubernetes.io/os = linux`).

| Resource | Description |
|----------|-------------|
| **PersistentVolumeClaim** | `mssql-data-pvc` — Premium SSD (`managed-csi-premium`), configurable size (`sql_storage_size`, default 128 GiB). Mounted as two sub-paths: `/var/opt/mssql/data` and `/var/opt/mssql/log`. |
| **Secret** | `mssql-secret` — Holds the SA password, injected as the `MSSQL_SA_PASSWORD` env var. |
| **Service** | `mssql-service` — ClusterIP on port 1433 (internal only). |
| **NetworkPolicy** | `mssql-allow-windows-app-only` — Restricts ingress to pods labeled `app-type: windows-servicetier` on port 1433. External SQL access is granted temporarily per-user via `AllowSqlAccess` (creates a separate LoadBalancer + NetworkPolicy). |

An init container (`fix-permissions`) runs as root to `chown` the data and log directories to the `mssql` user (UID 10001) before SQL Server starts.

All BC container databases live in this single SQL Server instance. Each container gets its own database, restored from a `.bak` backup via `sqlcmd`. When a container is removed, its database is dropped. The PVC ensures data survives pod restarts and node reboots.

## Deployment

### Overview

There are two ways to deploy the infrastructure:

| Method | Script / Workflow | When to use |
|--------|-------------------|-------------|
| **Local deploy** | `terraform/deploy.ps1` | First-time setup, or full redeploy from a developer workstation |
| **Full deploy workflow** | `.github/workflows/DeployFkhFullStack.yml` | Same as `deploy.ps1` but runs as a GitHub Actions workflow (manual trigger) |
| **Backend-only workflow** | `.github/workflows/UpdateFkhBackEnd.yml` | Publish function code only — no Terraform changes |

Both the local script and the `DeployFkhFullStack` workflow perform the **same steps**. The workflow reimplements the `deploy.ps1` logic as individual workflow steps. The only differences are:

- **Authentication** — `deploy.ps1` uses the user's own `az login` session (the user needs `Contributor` + `User Access Administrator` on the subscription); the workflow uses OIDC via the deploy identity (App Registration or Managed Identity).
- **Secrets** — `deploy.ps1` recovers secrets from Terraform state on re-deploys (or prompts interactively); the workflow gets them from GitHub Actions secrets.

### Deploy Steps

Both `deploy.ps1` and `DeployFkhFullStack` run the following steps in order:

- **Ensure state storage** — Idempotently creates a resource group (`fkh-<org>-state`), a storage account (`fkh<org>state`), and a `tfstate` blob container if they don't already exist. Grants `Storage Blob Data Contributor` on the state account (to the current user for `deploy.ps1`, to the OIDC service principal for the workflow).
- **Terraform init** — Initializes with the Azure blob backend (uses Azure AD auth, not storage keys).
- **Resolve secrets** — `deploy.ps1` reads `TF_VAR_sql_sa_password` and `TF_VAR_github_app_private_key` from existing Terraform state to avoid re-prompting, then interactively prompts for any still missing. The workflow receives them from GitHub Actions secrets (`SQL_SA_PASSWORD`, `GH_APP_PRIVATE_KEY`).
- **Bootstrap apply** — Targeted `terraform apply` for core Azure resources (resource group, AKS, ACR, storage accounts, function app, managed identity, federated credentials) so the Kubernetes provider can initialize.
- **Fetch AKS credentials** — Runs `az aks get-credentials` so subsequent steps can talk to the cluster.
- **Check / import GitHub teams** — Runs `checkGitHubTeam.ps1` to import existing GitHub teams into Terraform state, avoiding conflicts.
- **Full Terraform apply** — Applies all remaining resources (Kubernetes namespace, SQL Server pod, network policies, GitHub teams, monitoring).
- **Publish function code** — Runs `func azure functionapp publish` with the .NET isolated worker (`deploy.ps1` delegates to `deploy-functionupdate.ps1`).
- **Sync GitHub Actions secrets** — Uses `gh secret set` to push `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`, `ACR_LOGIN_SERVER`, and `DBS_STORAGE_ACCOUNT` to the repository for the `CreateImages` workflow.

### Organization Configuration (`.tfvars`)

Each organization has a file under `terraform/organizations/<org>.tfvars`. Copy `deployment-repo/config/deployment.tfvars` as a starting point.

| Variable | Description |
|----------|-------------|
| `subscription_id` | Azure subscription to deploy into |
| `tenant_id` | Azure AD tenant ID |
| `location` | Azure region (e.g. `swedencentral`) |
| `fkhDeploymentName` | Short identifier for this deployment, used in resource names (`fkh-<name>-*`) |
| `github_org` | GitHub organization name for team membership (case-sensitive) |
| `github_team_name` | Team controlling user access (created if missing) |
| `github_team_members` | List of GitHub usernames to add to the team |
| `github_admin_team_name` | Team controlling admin access |
| `github_admin_team_members` | Admin usernames |
| `allowed_org_teams` | Org/team pairs the Function App accepts for user auth |
| `admin_org_teams` | Org/team pairs that grant admin privileges |
| `support_org_teams` | Org/team pairs that grant support access |
| `allowed_users` | Explicit GitHub usernames and roles (`admin`, `member`, or `support`) |
| `allowed_oidc_repos` | Repos allowed to call the Function App via GitHub Actions OIDC |
| `github_app_id` | GitHub App ID (triggers image builds) |
| `github_app_installation_id` | Installation ID of the GitHub App |
| `create_images_repo` | GitHub org/repo where the Create Images workflow runs |
| `contact_email_for_letsencrypt` | Email for Let's Encrypt certificates |
| AKS sizing | `linux_vm_size`, `windows_vm_size`, `windows_min/max_node_count`, spot pool settings, `windows_overprovision`, `windows_prepull_images` |
| SQL | `sql_storage_size`, `namespace` |

**Secrets** (never committed to tfvars — set via environment variables):

| Environment Variable | Description |
|----------------------|-------------|
| `TF_VAR_sql_sa_password` | SA password for the SQL Server pod (≥ 8 characters) |
| `TF_VAR_github_app_private_key` | PEM-encoded private key of the GitHub App |

### App Registrations and Identities

There are two distinct identity mechanisms:

#### 1. Managed Identity (runtime — created by Terraform)

Terraform creates a **user-assigned managed identity** (`identity.tf`) that the Function App uses at runtime to access Azure resources. No credentials are stored — Azure handles authentication at the infrastructure level.

| Role Assignment | Scope | Purpose |
|-----------------|-------|---------|
| Azure Kubernetes Service Contributor | AKS cluster | Manage containers, exec into pods |
| Storage Blob Data Contributor | DBS storage account | Generate SAS URLs for database backups |
| AcrPush | Container Registry | Push images (used by GitHub Actions via federated credential) |
| Log Analytics Reader | Log Analytics workspace | Query image pull timestamps |

The same identity has a **federated credential for GitHub Actions OIDC** (`identity.tf`):

```
Issuer:   https://token.actions.githubusercontent.com
Subject:  repo:<org>/<repo>:ref:refs/heads/main
Audience: api://AzureADTokenExchange
```

This lets the `CreateImages` workflow authenticate to Azure as the managed identity (passwordless) to push images to ACR and upload database backups to Blob Storage.

#### 2. Deploy Identity (created manually, workflows only)

The `DeployFkhFullStack` and `UpdateFkhBackEnd` **workflows** need an Azure identity to authenticate via OIDC. This can be either an **App Registration** or a **User-Assigned Managed Identity** — see [Azure Setup (Path A)](docs/AzureSetup-PathA.md) for both options. (`deploy.ps1` does **not** use this — it relies on the user's own `az login` session, which must have `Contributor` + `User Access Administrator` on the subscription.)

Whichever option you choose, configure:

- A **federated credential** for GitHub Actions OIDC:
  ```
  Issuer:   https://token.actions.githubusercontent.com
  Subject:  repo:<org>/<repo>:ref:refs/heads/main
  Audience: api://AzureADTokenExchange
  ```
- **Role assignments** on the Azure subscription:
  - `Contributor` — create and manage all resources
  - `User Access Administrator` — create role assignments for managed identities

This identity's client ID is stored in `config/deployment.tfvars` as `azure_deploy_client_id`. The `tenant_id` and `subscription_id` are also read from the same file.

### GitHub App

A **GitHub App** is required to trigger image-build workflows when a user requests a BC version whose image doesn't exist in ACR yet. The Function App uses the GitHub App to dispatch the `CreateImages` workflow.

Configuration:

| Setting | Where it goes |
|---------|---------------|
| App ID | `github_app_id` in `.tfvars` |
| Installation ID | `github_app_installation_id` in `.tfvars` |
| Create Images repo | `create_images_repo` in `.tfvars` (org/repo format) |
| Private key (.pem) | `TF_VAR_github_app_private_key` env var → stored in Function App settings as `GITHUB_APP_PRIVATE_KEY` |

The `GitHubAppTokenService` in the backend creates short-lived JWTs signed with the private key, exchanges them for installation access tokens, and uses those to call `POST /repos/{owner}/{repo}/actions/workflows/CreateImages.yml/dispatches`.

### GitHub Actions Secrets and Variables

#### Secrets set automatically by `deploy.ps1` (Step 11)

These are synced from Terraform outputs for the `CreateImages` workflow:

| Secret | Value source |
|--------|-------------|
| `AZURE_CLIENT_ID` | Managed identity client ID |
| `AZURE_TENANT_ID` | Terraform `tenant_id` output |
| `AZURE_SUBSCRIPTION_ID` | Terraform `subscription_id` output |
| `ACR_LOGIN_SERVER` | ACR login server FQDN |
| `DBS_STORAGE_ACCOUNT` | DBS storage account name |

#### Secrets set manually (for deploy workflows)

| Secret | Purpose |
|--------|---------|
| `SQL_SA_PASSWORD` | SQL Server SA password |
| `GH_APP_PRIVATE_KEY` | GitHub App PEM private key |

> The deployment identity client ID (`azure_deploy_client_id`) is in `config/deployment.tfvars` — it is not a secret.

#### Secrets for client publishing (`DeployFkhClients`)

| Secret / Variable | Purpose |
|-------------------|---------|
| `VSCE_PAT` | VS Code Marketplace publish token |
| `OVSX_PAT` | Open VSX Registry publish token (Cursor extension marketplace) |
| `AZURE_KEY_VAULT_CLIENT_ID` | App Registration for code signing via Azure Key Vault |
| `AZURE_KEY_VAULT_TENANT_ID` | Tenant for Key Vault access |
| `AZURE_KEY_VAULT_SUBSCRIPTION_ID` | Subscription for Key Vault access |
| `AZURE_KEY_VAULT_URL` | Key Vault URL for code signing |
| `AZURE_KEY_VAULT_CERTIFICATE` (variable) | Certificate name in Key Vault |

#### Variables (non-secret)

| Variable | Purpose |
|----------|---------|
| `TFVARS_FILE` | Path to the organization `.tfvars` file (e.g. `organizations/freddydk.tfvars`) |

### Deployment Flow Diagram

```mermaid
graph TB
    subgraph "Shared Deploy Steps"
        S1["Ensure state storage<br/>(create RG + Storage + Blob if missing)"]
        S2["terraform init<br/>(Azure blob backend)"]
        S3["Resolve secrets"]
        S4["Bootstrap apply<br/>(AKS, ACR, MI, Function)"]
        S5["az aks get-credentials"]
        S6["checkGitHubTeam.ps1<br/>(import existing teams)"]
        S7["terraform apply<br/>(all resources)"]
        S8["func publish"]
        S9["gh secret set<br/>(sync CreateImages secrets)"]
        S1 --> S2 --> S3 --> S4 --> S5 --> S6 --> S7 --> S8 --> S9
    end

    LOCAL["deploy.ps1"] --> S1
    WF["DeployFkhFullStack"] --> S1

    LOCAL -.->|"secrets from state<br/>or interactive prompt"| S3
    WF -.->|"secrets from<br/>GitHub Actions secrets"| S3

    subgraph "Identities"
        USER["User's az login<br/>(Contributor + UAA)"]
        DEPLOY_APP["Deploy App Registration<br/>(Contributor + UAA)"]
        MI["Managed Identity<br/>(AKS + ACR + Storage)"]
        GH_APP["GitHub App<br/>(workflow dispatch)"]
    end

    LOCAL -.->|"interactive az login"| USER
    WF -.->|"OIDC"| DEPLOY_APP
    S7 --> MI
```
