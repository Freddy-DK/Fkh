# fkh-cli

Multi-targeted **.NET 8 / .NET 10** global tool (`fkh`) that proxies most operations to the Fkh backend via the **function catalog**, plus **client-only** commands that run locally (AL-Go overrides, deployment repo sync, file publish, etc.).

## Tech stack

- **C# / .NET 8 and .NET 10** console app
- **`PackAsTool`** → NuGet package `fkh`, command name `fkh`
- **Azure.Storage.Blobs** for some client-side flows
- Embedded **ALGoScripts** resources

Project file: `fkh-cli.csproj`.

## Directory structure

```
fkh-cli/
├── Program.cs              # Catalog fetch, dispatch, global options
├── ClientCommand.cs        # Base for client-only commands
├── TokenProvider.cs        # GH_TOKEN, OIDC, gh auth token
├── fkh.settings.json       # Default backend URL template (shipped with tool)
├── Commands/               # Client-only commands (not on server)
│   ├── PublishAppCommand.cs
│   ├── CreateDeploymentRepoCommand.cs
│   └── ...
└── ALGoScripts/            # Embedded resources for AL-Go integration
```

## Build commands

```powershell
cd fkh-cli
dotnet build fkh-cli.csproj
dotnet build fkh-cli.csproj -f net8.0
dotnet build fkh-cli.csproj -f net10.0
dotnet publish fkh-cli.csproj -c Release -f net10.0
dotnet pack -c Release -o ./nupkg
dotnet tool install --global --add-source ./nupkg fkh
```

VS Code: open folder and use **dotnet build** / **watch** tasks if configured.

Published via `.github/workflows/DeployFkhClients.yml` on changes under `fkh-cli/**` (NuGet.org + GitHub Packages).

## Test commands

No automated tests. Verify with:

```powershell
fkh --version
fkh -h
fkh listcontainers --backendUrl https://fkh-<org>-backend.azurewebsites.net/api
```

## Usage patterns

### Server commands (from catalog)

```powershell
fkh <command> --key "value" ...
fkh createcontainer --name mybc --artifactUrl "///us/latest" --adminUsername admin --adminPassword "..."
fkh listcontainers --asJson
```

Global options: `--useOIDC`, `--ghUser`, `--backendUrl`, `--nowait`, `--asJson`, `-h`, `--version`.

### Client-only commands

Registered in `ClientCommands` / `ClientCommand.cs` — e.g. `publishapp`, `createdeploymentrepo`, `updatedeploymentrepo`, `edit`. These do not appear on the backend catalog.

## Configuration precedence

1. `--backendUrl`
2. `FKH_BACKEND_URL` environment variable
3. `~/.fkh/settings.json`
4. `fkh.settings.json` next to the executable

## Authentication precedence

1. `--useOIDC` (GitHub Actions)
2. `GH_TOKEN`
3. `gh auth token` (optional `--ghUser`)

## Architecture patterns

- Fetch catalog from backend at startup; command names are **lowercase** matching catalog.
- Send protocol headers (`X-Fkh-Protocol-Version`, `X-Fkh-Client`) consistent with VSIX.
- Args: `--parameterName value` (catalog-driven parameter names).
- Long-running ops: support `--nowait` where backend returns job-style responses.

## Coding conventions

- Top-level statements in `Program.cs` for entry.
- New **server** commands: usually **no CLI code** — catalog discovery handles them.
- New **client** commands: subclass `ClientCommand`, register in `ClientCommands.All`.
- Bump `Version` in `fkh-cli.csproj` for releases; CI may use run number for prerelease flows.

## Related

- [fkh-backend/AGENTS.md](../fkh-backend/AGENTS.md) — API and catalog
- [deployment-repo/AGENTS.md](../deployment-repo/AGENTS.md) — `updatedeploymentrepo` command
