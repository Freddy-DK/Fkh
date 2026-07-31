# Installing Fkh

This guide walks you through a full Fkh installation using GitHub Actions workflows in a private deployment repository. You do not need local tooling for the installation; the deployment runs in GitHub Actions and Azure.

Use this page to confirm who needs to be involved before you start.

## Installation flow

| Step | File | Main owner | Outcome |
|---|---|---|---|
| 1 | [Create the private deployment repository](Step1-DeploymentRepo.md) | GitHub Organization Admin | A private repo that contains your deployment configuration and caller workflows |
| 2 | [Create the Azure deployment identity](Step2-AzureIdentity.md) | Azure Subscription Owner, and sometimes Entra ID Admin | A deployment identity that GitHub Actions can use to deploy to Azure |
| 3 | [Create the GitHub App](Step3-GitHubApp.md) | GitHub Organization Admin | A GitHub App that can trigger workflows and sync repository secrets |
| 4 | [Set up GitHub teams](Step4-GitHubTeams.md) | GitHub Organization Admin | Teams that control who can use and administer Fkh |
| 5 | [Configure your environment](Step5-ConfigureEnvironment.md) | GitHub Organization Admin | Completed `deployment.tfvars` and required GitHub Secrets |
| 6 | [Deploy](Step6-Deploy.md) | GitHub Organization Admin or repo maintainer | Fkh infrastructure deployed to Azure |

## Roles you need

An Fkh installation touches three systems:

- Azure subscription
- GitHub organization
- Microsoft Entra ID, formerly Azure AD

In a small organization, one person may hold all required permissions. In a larger organization, you may need three people. Confirm the roles below before starting.

### Azure Subscription Owner

This person has the **Owner** role on the Azure subscription where Fkh will be deployed.

They are responsible for:

- Creating or providing the target Azure subscription.
- Assigning **Contributor** and **User Access Administrator** to the deployment identity used by GitHub Actions.
- Approving Azure policy exemptions, if your organization requires them.

> **Why Owner is required:** assigning privileged roles such as **Contributor** and **User Access Administrator** at subscription scope requires the **Owner** role.

### GitHub Organization Administrator

This person can create repositories, GitHub Apps, teams, secrets, and variables in the GitHub organization.

They are responsible for:

- Creating the private deployment repository.
- Creating and installing the GitHub App.
- Creating GitHub teams for Fkh access control.
- Configuring GitHub Actions secrets and variables in the deployment repository.

> **Why a private deployment repository is used:** the deployment repository contains organization-specific configuration such as Azure subscription IDs, GitHub organization names, team names, and GitHub Secrets. It uses composite actions and reusable workflows from the public `Freddy-DK/Fkh` repository; you do not need to fork Fkh just to deploy it.

> **Optional fork:** fork Fkh only if you want to modify or contribute to the Fkh source code. You can then point the deployment workflows at your fork while testing changes.

### Entra ID administrator (application consent)

This person can grant Microsoft Graph application permissions to a Managed Identity or App Registration.

They are responsible for:

- Granting the deployment identity the **`Application.ReadWrite.OwnedBy`** Microsoft Graph permission, but only if you enable AAD container authentication.

> **Why this may be needed:** when AAD container authentication is enabled, the Fkh backend creates a dedicated Entra ID App Registration for each container. The Function App managed identity needs the `Application.ReadWrite.OwnedBy` Microsoft Graph permission to create and manage only the app registrations it owns. Terraform can grant that permission during deployment, but only if the deployment identity already has `Application.ReadWrite.OwnedBy`.

> **Who can usually do this:** someone with **Application Administrator**, **Cloud Application Administrator**, **Global Administrator**, or a role with equivalent permission in your Entra ID tenant. The exact role depends on your tenant configuration.

## Role summary

| Role | System | Minimum permission required |
|---|---|---|
| Azure Subscription Owner | Azure | **Owner** on the target subscription |
| GitHub Organization Admin | GitHub | Ability to create repos, GitHub Apps, teams, secrets, and variables |
| Entra ID Admin (app consent) | Microsoft Entra ID | Permission to grant application consent, required only for AAD container authentication |

> **Planning tip:** if these roles are held by different people, schedule a short coordination session before beginning. Step 2 is the main point where Azure and Entra ID responsibilities may overlap.

---

*Next step: [Step 1 — Create the Private Deployment Repository](Step1-DeploymentRepo.md)*
