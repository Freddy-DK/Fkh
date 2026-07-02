# Step 1 — Create the Private Deployment Repository

> **Performed by:** GitHub Organization Administrator

In this step you create the private repository that will hold your Fkh deployment configuration, secrets, and caller workflows.

## What you will need/create

| Item | Purpose |
|---|---|
| Fkh source repository | Provides reusable workflows, Terraform modules, backend code, CLI, and VS Code extension. Use the public `Freddy-DK/Fkh` repository, or optionally use your own fork if you need to modify or test Fkh source code. |
| Private deployment repository | Stores your organization-specific `deployment.tfvars`, GitHub Secrets, and caller workflows |

The deployment repository is private because it contains environment-specific configuration and stores secrets. The Fkh source repository can remain public because it should not contain your organization's secrets or deployment values.

Creating your own fork of `Freddy-DK/Fkh` is optional. Use the public `Freddy-DK/Fkh` repository unless you need to modify, test, or contribute changes to the Fkh source code.

## Repository model

Fkh uses a two-repository model:

| Repository | Visibility | Contains |
|---|---|---|
| Fkh source repository: `Freddy-DK/Fkh`, or your optional fork | Public | Reusable workflows, Terraform modules, backend code, CLI, VS Code extension |
| `fkh-deploy-<deploymentname>` | Private | `deployment.tfvars`, caller workflows, GitHub Secrets |

Each Fkh deployment needs its own deployment repository. You do not need a fork to create a deployment repository. If you do create a fork, you only need one Fkh fork per GitHub organization, even if you create several deployments.

Example:

```text
Freddy-DK/Fkh                              ← default public Fkh source repository
my-company/Fkh                             ← optional fork, only if you need one
my-company/fkh-deploy-contoso              ← deployment repo for Contoso
my-company/fkh-deploy-migrateFabrikam      ← deployment repo for Fabrikam migration
my-company/fkh-deploy-litware              ← deployment repo for Litware
```

Each deployment repository can have its own Azure subscription, configuration, secrets, and deployment identity.

> **Optional shared secrets:** if multiple deployments use the same Azure subscription and deployment identity, you can store shared values as GitHub organization-level secrets. Scope those secrets only to the deployment repositories that need them. You still need a separate federated credential for each deployment repository that uses the shared identity.

---

## 1.1 — Decide whether to fork Fkh

Creating your own fork of [`Freddy-DK/Fkh`](https://github.com/Freddy-DK/Fkh) is optional.

Use the public `Freddy-DK/Fkh` repository directly unless you need to modify Fkh source code, test changes, or contribute changes back to the project.

If you need a fork:

1. Open the [Fkh repository](https://github.com/Freddy-DK/Fkh) on GitHub.
2. Select **Fork**.
3. Under **Owner**, choose your GitHub organization.
4. Keep the repository name as `Fkh`.
5. Select **Create fork**.

> The fork is public by default. That is expected. It should not contain secrets or organization-specific configuration.

## 1.2 — Create the private deployment repository

You can create the private deployment repository with the Fkh CLI or manually in GitHub.

### Option A — Use the Fkh CLI

Run the following command:

```pwsh
fkh createdeploymentrepo --deploymentRepo org/repo [--fkhRepo fkhForkOrg/fkhForkRepo[@version]]
```

Replace `org/repo` with the GitHub organization and repository name for the private deployment repository.

Examples:

```pwsh
fkh createdeploymentrepo --deploymentRepo my-company/fkh-deploy-contoso
fkh createdeploymentrepo --deploymentRepo my-company/fkh-deploy-contoso --fkhRepo my-company/Fkh
fkh createdeploymentrepo --deploymentRepo my-company/fkh-deploy-contoso --fkhRepo my-company/Fkh@dev
```

Use `--fkhRepo` only when you created your own Fkh fork or want to pin a specific Fkh version. The optional `@version` suffix can be a tag, branch, `latest`, or `preview`; when omitted, the CLI uses `latest` from the selected source repository.

### Option B — Create the repository manually in GitHub

1. In your GitHub organization, go to **Repositories**.
2. Select **New repository**.
3. Name the repository using this pattern:

   ```text
   fkh-deploy-<deploymentname>
   ```

   Examples:

   ```text
   fkh-deploy-contoso
   fkh-deploy-migrateFabrikam
   fkh-deploy-litware
   ```

4. Set **Visibility** to **Private**.
5. Optionally select **Add a README file**.
6. Select **Create repository**.

> **Naming tip:** use a deployment name that makes the environment easy to recognize, such as a customer, region, team, or project name.

## 1.3 — Copy the deployment template

Skip this section if you created the deployment repository with the Fkh CLI. The CLI creates the repository and adds the starter files for you.

The Fkh repository includes a `deployment-repo/` folder with the starter configuration and workflow templates.

1. In your Fkh repository or fork, open the `deployment-repo/` folder.
2. Copy everything in that folder into the root of your new private deployment repository.
3. Confirm that the deployment repository looks like this:

```text
your-deployment-repo/
├── config/
│   └── deployment.tfvars
├── .github/
│   └── workflows/
└── README.md
```

You can copy the files using the GitHub web UI or by cloning both repositories locally and copying the files.

## 1.4 — Save the repository details

Record these values. You will need them in Step 2 when creating the federated credential for the deployment identity.

| Value | Example |
|---|---|
| GitHub organization name | `my-company` |
| Deployment repository name | `fkh-deploy-contoso` |
| Fkh repository | `Freddy-DK/Fkh`, or your fork such as `my-company/Fkh` |

> **Important:** the federated credential in Step 2 must reference the private deployment repository, not the Fkh source repository. GitHub Actions runs from the deployment repository, so the OIDC token uses the deployment repository name.

## Step complete

You now have a private deployment repository with the starter files in place.

---

*Previous: [Roles and Overview](README.md)*  
*Next: [Step 2 — Create the Azure Deployment Identity](Step2-AzureIdentity.md)*
