# Installation

**Documentation-only** folder: step-by-step guide to install Fkh using a **private deployment repository** and GitHub Actions (no local Terraform required for operators).

## Contents

| File | Topic |
|------|--------|
| [README.md](README.md) | Overview, roles, installation flow table |
| [Step1-DeploymentRepo.md](Step1-DeploymentRepo.md) | Private deployment repo from template |
| [Step2-AzureIdentity.md](Step2-AzureIdentity.md) | Azure deployment identity (OIDC) |
| [Step3-GitHubApp.md](Step3-GitHubApp.md) | GitHub App for workflows and secrets |
| [Step4-GitHubTeams.md](Step4-GitHubTeams.md) | Member, admin, and support teams; individual user access |
| [Step5-ConfigureEnvironment.md](Step5-ConfigureEnvironment.md) | `deployment.tfvars` and GitHub Secrets |
| [Step6-Deploy.md](Step6-Deploy.md) | Deploy Full Stack workflow |

## Tech stack

- **Markdown** documentation
- No code, build, or test commands

## Two-repo model (document this when editing)

| Repository | Visibility | Role |
|------------|------------|------|
| Fkh fork (this repo) | Public | Reusable workflows, Terraform, backend, clients |
| Org deployment repo | Private | `config/deployment.tfvars`, secrets, caller workflows |

Operators do **not** need to fork Fkh unless they modify source or test forks.

## Roles (for accurate docs)

- **Azure Subscription Owner** — subscription, deployment identity RBAC
- **GitHub Organization Admin** — repos, App, teams, Actions secrets
- **Entra ID admin** — only if AAD container authentication is enabled (`Application.ReadWrite.OwnedBy`)

## Conventions for contributors

- Keep steps actionable and ordered; link forward/back between steps.
- Never instruct committing secrets to `deployment.tfvars`.
- Reference workflow names consistently: **Deploy Full Stack**, **Update Backend**, **Create Images**.
- When product behavior changes (new secret, tfvars key, role), update the relevant Step file and Step 5/6 tables.

## Related templates & automation

- [deployment-repo/AGENTS.md](../deployment-repo/AGENTS.md) — template copied to private repo
- [fkh-cli/AGENTS.md](../fkh-cli/AGENTS.md) — `updatedeploymentrepo` syncs templates from fork

## Build / test

N/A — validate documentation by walking through a test deployment or reviewing links and workflow names against `.github/workflows/`.
