# Fkh Architecture

## Overview
Fkh is a platform for managing Business Central containers on Azure Kubernetes Service (AKS). It has three main components: a backend, a CLI, and a VS Code extension. All three are in the same monorepo.

## Components

### Backend (`fkh-backend/`)
- **Azure Functions** (.NET isolated worker, C#)
- Entry point: `Program.cs` — registers all services via DI
- Each function is a thin class inheriting `FunctionBase` (e.g. `PublishAppFunction.cs`)
- `FunctionBase` handles: GitHub token auth, team membership checks, brute-force protection, uniform HTTP responses
- Business logic lives in `Services/Fkh*.cs` classes (e.g. `FkhPublishApp`), all inherit `FkhServiceBase`
- `FkhServiceBase` provides AKS/ACR/Azure config from environment variables and Kubernetes client helpers
- **Function Catalog** (`FunctionCatalog.cs`): static list of `FunctionDefinition` objects describing every command (name, route, parameters with types/defaults/required flags)
- `GetFunctionCatalog` endpoint (`GET /functions`) serves the catalog as JSON (excludes `Hidden` functions)
- Two execution paths in `FunctionBase`: `ExecuteAsync` (JSON body) and `ExecuteWithFileAsync` (multipart/form-data for file uploads)
- `RetryAfterException` signals long-running operations — returns HTTP 202 with Retry-After header
- Auth: GitHub tokens (PAT or OIDC), validated via GitHub API. Team membership checked against `ALLOWED_ORG_TEAMS` / `ADMIN_ORG_TEAMS` env vars
- Some functions are `AdminOnly` (require admin team membership)

### Models (`fkh-backend/Models/`)
- `FunctionContracts.cs`: `FunctionDefinition`, `FunctionParameterDefinition`, `FunctionCatalogResponse`, `FunctionInvokeRequest`, `RetryAfterException`
- `GitHubModels.cs`: GitHub API response types
- `OrgTeamConfig.cs`: org/team configuration model

### CLI (`fkh-cli/`)
- .NET console app, entry point: `Program.cs` (top-level statements)
- **Generic catalog-driven**: fetches function catalog from backend, matches `args[0]` to a function name, parses `--key "value"` args, POSTs to the function route
- Handles file-type parameters (multipart upload), retry loops (HTTP 202), `--nowait`
- **Output**: Backend always returns JSON. By default the CLI formats it as human-readable text (`FormatJsonAsText`). When `--asJson` is specified, the raw JSON is pretty-printed instead
- Auth: OIDC token > GH_TOKEN env var > `gh auth token` CLI
- Backend URL: `--backendUrl` > `FKH_BACKEND_URL` env var > `~/.fkh/settings.json` > `fkh.settings.json` next to exe
- **Client-side commands** (`ClientCommand.cs`, `Commands/`): commands that run locally, not via backend catalog. Examples: `CopyFromContainer`, `CopyToContainer`, `DownloadDatabase`, `UploadDatabase`, `Edit`, `Open`, `Status`, `CreateDeploymentRepo`, `UpdateDeploymentRepo`, `PoorMansTerminal`
- Client commands are checked first before the catalog lookup

### VS Code Extension (`fkh-vsix/`)
- TypeScript, entry point: `src/extension.ts`
- **Generic catalog-driven** (same as CLI): `fkh.run` command fetches catalog, shows quick pick of all functions, prompts for parameters dynamically
- `getFunctionCatalog()` calls `GET /functions` on the backend
- `invokeFunctionByName()` + `promptForParameters()` handle dynamic UI: file pickers for `file`-type params, input box for single param, webview form for multiple params
- Parameter defaults: prefilled values > VS Code settings (`fkh.<FunctionName>.<paramName>`) > auto-detect (e.g. IP) > catalog defaults
- Admin-only params hidden from non-admins (based on `vmsProvider.visible`)
- Tree views: Projects (from AL-Go settings), Containers, Images, VMs (admin only)
- Some functions have dedicated tree-view commands for convenience (start/stop/remove container, etc.)
- Auth: VS Code GitHub authentication API (`read:user`, `read:org` scopes)
- Multi-account and multi-backend support via `fkh.backendUrl` setting (string or account-keyed object)
- `containersTreeProvider.ts`: tree data providers for Projects, Containers, Images, VMs
- `readALGoSettings.ts`: reads AL-Go repository settings for project discovery
- `updateLaunchJson.ts`: updates VS Code launch.json after container creation

### Infrastructure (`terraform/`)
- Terraform (>= 1.7): provisions AKS cluster, ACR, Azure Functions, managed identity, monitoring
- Providers: azurerm ~4.0, azuread ~2.47, kubernetes ~2.30, helm ~2.14
- Backend: Azure Storage (azurerm)
- Key files: `main.tf`, `kubernetes.tf`, `function.tf`, `acr.tf`, `identity.tf`, `monitoring.tf`, `variables.tf`, `outputs.tf`
- Deployment scripts: `deploy.ps1`, `deploy-functionupdate.ps1`

### Deployment Repo (`deployment-repo/`)
- Template/config for GitHub Actions deployment workflows

## Key Patterns

### Terminology: "adding a function"
- **"Adding a function"** (no qualifier) means adding a catalog-driven function in the backend that automatically appears in **both** the CLI and the VS Code extension via the function catalog.
- **"Adding a function in the CLI"** means adding a **client-side command** that only exists in the CLI (`ClientCommand.cs` / `Commands/`). It may still call backend endpoints or require new backend functionality, but it must **not** be added to the VS Code extension.

### Adding a new function (backend + both clients)
1. Add a `FunctionDefinition` to `FunctionCatalog.cs` (name, route, parameters)
2. Create a service class in `Services/Fkh<Name>.cs` inheriting `FkhServiceBase`
3. Create a function class `<Name>Function.cs` inheriting `FunctionBase`
4. Register the service in `Program.cs` DI container
5. The function automatically appears in CLI and VS Code extension via the catalog

### Adding a new function in the CLI (client-side only)
1. Create a new command class in `fkh-cli/Commands/` (see existing commands for patterns)
2. Register the command in `ClientCommand.cs` so it is checked before the catalog lookup
3. If backend functionality is needed, add the required backend endpoint(s) as above, but do **not** add them to `FunctionCatalog.cs` (or mark them `Hidden`) so they stay out of the VS Code extension's catalog-driven UI
4. The command does **not** appear in the VS Code extension

### Parameter types
- `string`: text input
- `boolean`: checkbox (vsix) / flag (cli)
- `file`: file picker (vsix) / file path (cli), sent as multipart/form-data

### Auth flow
- Clients send GitHub token as `Authorization: Bearer <token>`
- Backend validates via GitHub API, checks org/team membership
- OIDC tokens supported for GitHub Actions CI/CD scenarios
- Brute-force protection: 3 failed attempts per IP within 5 minutes = blocked

### Security principle: no human-managed secrets
- The entire setup uses managed identities, OIDC federation, and platform-provided credentials only
- There are no human-created secrets, certificates, or API keys that need manual rotation or cycling
- This is by design and must stay this way — never introduce static secrets, connection strings with keys, or manually provisioned certificates

## Handling Breaking Changes

When a change would break existing clients, use one of the following strategies to maintain backward compatibility during migration.

### Per-endpoint breaking changes (response shape, parameters, behavior)

Use **route-based versioning** to run old and new versions side by side:

1. Set `Hidden = true` on the existing `FunctionDefinition` in `FunctionCatalog.cs` — old clients that already know the route keep working, but the function no longer appears in the catalog for new clients
2. Create a new `FunctionDefinition` with a versioned route (e.g. `Route = "v2/GetAppInfo"`) — this becomes the catalog-visible entry
3. Add a new function class (e.g. `GetAppInfoV2Function.cs`) with the v2 route, pointing at either a new or shared service method
4. Both routes remain live — old clients hit the original route, new clients discover the v2 route from the catalog
5. Both the CLI and VS Code extension automatically pick up the new function from the catalog without any client-side changes

Once all clients have migrated, remove the old function class and hidden catalog entry.

### Cross-cutting breaking changes (authentication, authorization, request format)

For changes that affect every endpoint (e.g. switching auth token format), **do not duplicate routes**. Instead, extend `FunctionBase.AuthenticateAndAuthorizeAsync` with multi-scheme detection:

1. Detect the new auth/request format from the incoming request (header value, token structure, content type, etc.)
2. Branch to the appropriate validation logic within `AuthenticateAndAuthorizeAsync`
3. Old clients continue sending the old format; new clients send the new format; the backend accepts both

This is already the established pattern — `FunctionBase` detects GitHub OIDC tokens vs. GitHub PATs by inspecting the token format and branches accordingly. Any new auth scheme is just another branch in the same pipeline. No function classes, routes, or catalog entries need to change.

Once all clients have migrated, remove the old auth branch and return 401 for the retired format.

## Prerequisites

### Hard prerequisites
These are required for the CLI or VS Code extension to function at all. **Do not add new hard prerequisites without explicit approval.**

#### CLI (`fkh-cli`)
- **.NET 8.0 runtime** — the CLI targets `net8.0`
- **GitHub authentication** — one of the following must be available (checked in order):
  1. `--useOIDC` (GitHub Actions OIDC)
  2. `GH_TOKEN` environment variable (PAT)
  3. `gh auth token` (requires `gh` CLI installed and authenticated)
- **Backend URL** — resolved in order:
  1. `--backendUrl` CLI argument
  2. `FKH_BACKEND_URL` environment variable
  3. `~/.fkh/settings.json`
  4. `fkh.settings.json` next to the executable

#### VS Code Extension (`fkh-vsix`)
- **VS Code >= 1.85.0**
- **GitHub authentication** — handled via VS Code's built-in `vscode.authentication` API (scopes: `read:user`, `read:org`). No external tools needed.
- **Backend URL** — configured via `fkh.backendUrl` VS Code setting (string or account-keyed object), or prompted interactively

### Optional prerequisites (CLI only)
These external tools are not required but enable additional functionality in specific CLI client-side commands. The VS Code extension has **no** optional external tool dependencies — it communicates with the backend purely over HTTP.

| Tool | Commands that use it | Behavior when available | Behavior when unavailable |
|---|---|---|---|
| `gh` (GitHub CLI) | `CreateDeploymentRepo` | Creates a private GitHub repo via `gh repo create` | Command fails |
| `gh` (GitHub CLI) | `UpdateDeploymentRepo` | Clones repo, fetches templates via `gh api` | Command fails |
| `gh` (GitHub CLI) | `Open` | Resolves GitHub username via `gh api user` for pod label lookup | Falls back to manual pod name entry (only used when `kubectl` is also unavailable) |
| `kubectl` | `Open` | Finds the pod via `kubectl get pods` and opens an interactive `kubectl exec` PowerShell session in the container | Falls back to `PoorMansTerminal` (backend-based interactive shell) |
| `git` | `UpdateDeploymentRepo` | Commits and pushes updated deployment repo files | Command fails |
