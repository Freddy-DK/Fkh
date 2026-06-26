# fkh-backend

Azure Functions **v4** app (.NET 8 isolated worker) that authenticates GitHub users and orchestrates Business Central containers on AKS.

## Tech stack

- **C# / .NET 8** — `Microsoft.Azure.Functions.Worker`
- **Azure SDKs:** Identity, AKS, ACR, Blob Storage, Monitor
- **KubernetesClient** — deployments, pods, scaling
- **JWT / OIDC** — GitHub token validation
- **Microsoft Graph** — Entra app registration (when AAD container auth is enabled)

Project file: `azure-function.csproj`.

## Directory structure

```
fkh-backend/
├── Program.cs              # DI: register all Fkh* services, JsonSerializerOptions
├── FunctionBase.cs         # Auth, teams, brute-force protection, HTTP helpers
├── FunctionCatalog.cs      # API metadata for clients
├── *Function.cs            # One HTTP trigger per operation (thin)
├── Services/
│   ├── FkhServiceBase.cs   # AKS/K8s config from environment variables
│   └── Fkh*.cs             # Business logic per operation
├── Models/                 # DTOs, GitHub models, protocol types
├── AL-Go/                  # Artifact URL resolution helpers
├── scripts/                # Copied to build output
└── .vscode/                # build, publish, watch tasks
```

`ContainerScripts/**` is included from `../ContainerScripts` via the csproj and published with the app.

## Build commands

```powershell
cd fkh-backend
dotnet build azure-function.csproj
dotnet publish azure-function.csproj -c Release
dotnet watch run --project azure-function.csproj   # local dev (port 7071)
```

VS Code tasks (`.vscode/tasks.json`): **build**, **publish**, **watch**.

Deploy to Azure (after infra exists):

```powershell
func azure functionapp publish fkh-<deployment>-backend --dotnet-isolated
```

CI: `.github/workflows/DeployFkhFullStack.yml`, `UpdateFkhBackEnd.yml` — .NET 8 + Azure Functions Core Tools v4.

## Test commands

No test project in this repo. Manually exercise endpoints via CLI/VSIX or local `func`/watch.

## Architecture patterns

### Thin functions, fat services

- HTTP triggers inherit `FunctionBase` for auth and response shaping.
- Put logic in `Services/Fkh*.cs` registered as singletons in `Program.cs`.
- Route names match catalog entries (e.g. `Route = "CreateContainer"`).

### Catalog-driven API

- `FunctionCatalog` lists operations, parameters, and admin-only flags.
- Clients call `GET /api/functions` — keep catalog in sync when adding endpoints.

### Configuration

- Environment-driven via `FkhServiceBase`: `AKS_*`, `WEBSITE_HOSTNAME`, team lists, storage, ACR, etc.
- Set in Terraform (`terraform/function.tf`) for deployed apps.

### JSON

- Global `JsonSerializerOptions` with `PropertyNamingPolicy = JsonNamingPolicy.CamelCase` in `Program.cs`.

## Coding conventions

- Namespace: `Fkh`
- Service classes: `FkhCreateContainer`, `FkhListContainers`, etc.
- Enable nullable reference types; use `ImplicitUsings`.
- Admin-only operations: gate in `FunctionBase` / catalog `adminOnly` metadata.
- Do not bypass GitHub team checks for new endpoints.

## Adding a new backend operation

1. Create `Services/FkhYourOperation.cs` extending or using `FkhServiceBase`.
2. Register in `Program.cs`.
3. Add `YourOperationFunction.cs` with `[HttpTrigger]` and inherit `FunctionBase`.
4. Add entry to `FunctionCatalog.cs`.
5. If request shape changes for all clients, update `SupportedClientVersions.json` at repo root.

## Related folders

- [`../ContainerScripts/`](../ContainerScripts/AGENTS.md) — scripts served to containers
- [`../terraform/`](../terraform/AGENTS.md) — Function App settings and identity
- [`../fkh-cli/`](../fkh-cli/AGENTS.md), [`../fkh-vsix/`](../fkh-vsix/AGENTS.md) — consumers of this API
