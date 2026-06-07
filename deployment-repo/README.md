# Fkh Deployment Repository

This is a private deployment repository for your [Fkh](https://github.com/Freddy-DK/Fkh) infrastructure. It contains your Terraform configuration and thin caller workflows that delegate to reusable workflows in your Fkh fork.

## Two-Repo Model

| Repository | Visibility | Purpose |
|---|---|---|
| **Your Fkh fork** | Public | Contains the reusable workflows, Terraform modules, backend code, and CLI |
| **This repo** (deployment) | Private | Contains your `deployment.tfvars` and caller workflows that pass secrets |

## Setup

Follow the [Fkh Installation Guide](https://github.com/Freddy-DK/Fkh/blob/main/Installation/README.md) to complete the setup. Here is a summary of the remaining steps:

### 1. Create the Azure Deployment Identity

Create a Managed Identity or App Registration for GitHub Actions to authenticate with Azure using OIDC. See [Step 2 — Azure Identity](https://github.com/Freddy-DK/Fkh/blob/main/Installation/Step2-AzureIdentity.md).

### 2. Create the GitHub App

Create a GitHub App in your organization and install it on this deployment repository. The app is used to dispatch image-build workflows and sync secrets. See [Step 3 — GitHub App](https://github.com/Freddy-DK/Fkh/blob/main/Installation/Step3-GitHubApp.md).

### 3. Set up GitHub Teams

Create member, admin, and support teams in your GitHub organization to control access — or grant individual users access directly. See [Step 4 — GitHub Teams](https://github.com/Freddy-DK/Fkh/blob/main/Installation/Step4-GitHubTeams.md).

### 4. Configure `config/deployment.tfvars`

Edit `config/deployment.tfvars` with your Azure subscription, GitHub org, team names, and other settings. See [Step 5 — Configure Environment](https://github.com/Freddy-DK/Fkh/blob/main/Installation/Step5-ConfigureEnvironment.md).

**Never commit secrets** to this file. Use GitHub Secrets instead (see below).

### 5. Set up GitHub Secrets

Go to **Settings > Secrets and variables > Actions** and add:

| Secret | Description |
|---|---|
| `SQL_SA_PASSWORD` | SA password for the SQL Server deployed in AKS |
| `GH_APP_PRIVATE_KEY` | PEM-encoded private key of the GitHub App |

> The deployment identity client ID (`azure_deploy_client_id`) is configured in `config/deployment.tfvars` — it is not a secret.

### 6. Deploy

Run the **Deploy Full Stack** workflow from the Actions tab to create all Azure infrastructure and publish the backend. See [Step 6 — Deploy](https://github.com/Freddy-DK/Fkh/blob/main/Installation/Step6-Deploy.md).

After the initial deployment, use **Update Backend** to publish code changes without re-running Terraform.

### 7. Create Images

The **Create Images** workflow can run either:
- **In the public Fkh repository or your fork** (free, but publicly visible which images you build)
- **In this private repo** (paid runners, but private)

The Fkh backend dispatches image builds to whichever repository ran the most recent `Deploy Full Stack` workflow (the deployment workflow sets `create_images_repo` automatically from the GitHub Actions context).

## Updating This Deployment Repository

Workflow templates and other supporting files in this deployment repository can be refreshed from the upstream Fkh repository (or your fork) using the Fkh CLI.

```pwsh
fkh updatedeploymentrepo --deploymentRepo org/repo [--fkhRepo fkhForkOrg/fkhForkRepo[@branch]]
```

Examples:

```pwsh
fkh updatedeploymentrepo --deploymentRepo my-company/fkh-deploy-contoso
fkh updatedeploymentrepo --deploymentRepo my-company/fkh-deploy-contoso --fkhRepo my-company/Fkh
fkh updatedeploymentrepo --deploymentRepo my-company/fkh-deploy-contoso --fkhRepo my-company/Fkh@dev
```

The optional `@branch` suffix controls which branch of the Fkh fork is used for fetching templates and for the reusable workflow references in the caller workflows. When omitted it defaults to `main`. This is useful for testing Fkh changes on a feature branch without affecting other deployments.

What it does:

- Clones the deployment repository to a temporary folder.
- Fetches every file from the `deployment-repo/` folder of the Fkh repository (or fork) at the specified branch and writes it into the deployment repo, creating any missing folders.
- In `.yml` files, rewrites the default `Freddy-DK/Fkh` reference and `@main` branch reference to your `--fkhRepo` value when one is provided.
- **Never overwrites `config/deployment.tfvars`** — your environment-specific configuration is preserved.
- Commits and pushes the changes using your `gh` user identity.

Use this command whenever the upstream Fkh repository ships changes to the workflow templates so your caller workflows stay in sync.

Requirements:

- The [Fkh CLI](https://github.com/Freddy-DK/Fkh/tree/main/fkh-cli) installed locally.
- The GitHub CLI (`gh`) installed and authenticated with write access to the deployment repository.
