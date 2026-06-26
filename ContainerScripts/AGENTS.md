# ContainerScripts

**PowerShell** scripts executed inside Business Central containers during first-run setup and image builds. They configure the database, tenant, certificates, web configuration, and additional BC container settings.

## Tech stack

- **PowerShell** (BcContainer / Business Central container conventions)
- No build step — scripts are **content-linked** into the backend and served over HTTPS

## Directory structure

```
ContainerScripts/
├── SetupDatabase.ps1
├── SetupTenant.ps1
├── SetupConfiguration.ps1
├── SetupCertificate.ps1
├── SetupVariables.ps1
├── SetupWebConfiguration.ps1
├── AdditionalSetup.ps1
└── prompt.ps1
```

## How scripts reach containers

1. `fkh-backend/azure-function.csproj` links `../ContainerScripts/**` as content copied to output.
2. Deployed Function App serves scripts at URLs under `/api/containerscripts/ContainerScripts/...`.
3. `FkhServiceBase` sets container environment so BC setup downloads scripts from the Function App hostname (`FoldersValue` / similar env).

## Build / deploy

No separate build. Changes deploy with **backend** publish:

```powershell
cd fkh-backend
dotnet publish azure-function.csproj -c Release
# or CI Update Backend / Full Stack workflow
```

**Create Images** workflow (`.github/workflows/CreateImages.yml`) may inline or reference `SetupTenant.ps1` when building BC images with BcContainerHelper on `windows-2022` runners.

## Test commands

No automated tests. Validate by:

- Creating a container via CLI/VSIX and checking container logs.
- Running **Create Images** workflow for image pipeline changes.

## Architecture patterns

- Scripts assume standard **BcContainer** setup flow (database restore, tenant mount, web client, etc.).
- Keep scripts **idempotent where possible** — containers may retry setup steps.
- Order matters: database before tenant before web configuration; follow existing script sequence when adding steps.
- Avoid secrets in scripts — pass via container environment variables set by backend/K8s manifests.

## Coding conventions

- Use clear, descriptive script names (`Setup*.ps1`).
- Match existing error handling style (`throw` / `$ErrorActionPreference` patterns in sibling files).
- Comment only non-obvious BC or SQL behavior.
- Coordinate with `fkh-backend/Services/FkhCreateContainer.cs` (and related) when new env vars are required.

## Related

- [fkh-backend/AGENTS.md](../fkh-backend/AGENTS.md)
- [.github/workflows/CreateImages.yml](../.github/workflows/CreateImages.yml)
