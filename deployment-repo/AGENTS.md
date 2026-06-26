# deployment-repo

**Template** for an organization's **private** Fkh deployment repository. Not runnable infrastructure by itself — it holds org-specific Terraform variables and thin GitHub Actions workflows that call reusable workflows in the public Fkh repository (or your fork).

## Tech stack

- **GitHub Actions** (`workflow_call`)
- **Terraform variables** (`config/deployment.tfvars`)
- No application source code

## Directory structure

```
deployment-repo/
├── README.md
├── config/
│   └── deployment.tfvars    # Org config — NEVER commit secrets here
└── .github/workflows/
    ├── DeployFullStack.yml   # → Freddy-DK/Fkh/.../DeployFkhFullStack.yml
    ├── UpdateBackend.yml       # → UpdateFkhBackEnd.yml
    └── CreateImages.yml        # → CreateImages.yml
```

## Deploy commands (operators)

All via **GitHub Actions** UI (`workflow_dispatch`) in the private repo:

| Workflow | Purpose |
|----------|---------|
| **Deploy Full Stack** | Terraform + mssql-fts image + function publish + optional web |
| **Update Backend** | Backend (and web) code only, no full Terraform |
| **Create Images** | Build BC images to ACR (`artifactUrls` input) |

### Required GitHub Secrets

| Secret | Description |
|--------|-------------|
| `SQL_SA_PASSWORD` | SQL Server SA password for in-cluster SQL |
| `GH_APP_PRIVATE_KEY` | GitHub App PEM private key |

`azure_deploy_client_id` belongs in `deployment.tfvars` (not a secret).

## Syncing templates from upstream

When Fkh ships workflow or template changes:

```powershell
fkh updatedeploymentrepo --deploymentRepo org/repo [--fkhRepo forkOrg/Fkh[@branch]]
```

- Overwrites files from `deployment-repo/` folder in the Fkh fork.
- **Never overwrites** `config/deployment.tfvars`.
- Rewrites `Freddy-DK/Fkh@main` references in YAML when `--fkhRepo` is set.

Requires [fkh CLI](../fkh-cli/AGENTS.md) and `gh` with write access.

## Architecture patterns

- **Separation of concerns:** secrets and subscription IDs stay private; reusable logic stays in public Fkh.
- Caller workflows pass `secrets: inherit` and `var-file` path to reusable workflows.
- `create_images_repo` is set by deploy workflow context so image builds target the right repo.
- Fork testing: point workflows at `myorg/Fkh@feature-branch` via tfvars or `updatedeploymentrepo --fkhRepo`.

## Conventions for editing this template

- Keep caller workflows minimal — only inputs, secrets mapping, and `uses:` to reusable workflows.
- Document every new `deployment.tfvars` key in [Installation/Step5-ConfigureEnvironment.md](../Installation/Step5-ConfigureEnvironment.md).
- Use placeholder values in `deployment.tfvars` (zeros for GUIDs, `myorg` for name).
- Do not add application code to this template.

## Test commands

N/A. Validation is running workflows in a test deployment repo against a non-production Azure subscription.

## Related

- [Installation/AGENTS.md](../Installation/AGENTS.md)
- [terraform/AGENTS.md](../terraform/AGENTS.md)
- Root [.github/workflows/](../.github/workflows/)
