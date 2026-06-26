# Setup Azure DevOps Service Connection

This guide walks through setting up an Azure DevOps pipeline that can call the fkh CLI using OIDC authentication. After completing these steps, Azure Pipelines can provision and manage Business Central containers without storing any secrets.

## Prerequisites

- A deployed Fkh environment (completed Steps 1–6)
- An Azure DevOps organization and project
- Owner/Admin access to the Azure DevOps project

## Step 1 — Get your Azure DevOps Organization ID

Open the following URL in your browser (replace `<org>` with your organization name):

```
https://dev.azure.com/<org>/_apis/connectiondata
```

Find the `instanceId` value in the JSON response — this is your `devops_org_id`.

## Step 2 — Add `allowed_ado_connections` to your deployment config

In your private deployment repository, edit `config/deployment.tfvars` and add your connection:

```hcl
allowed_ado_connections = [
  {
    devops_org_id          = "<your-org-id>"           # instanceId from Step 1
    devops_org             = "<your-org-name>"         # Azure DevOps organization name
    devops_project         = "<your-project-name>"     # Azure DevOps project name
    devops_connection_name = "<connection-name>"       # Name you'll give the service connection (e.g. "FkhConnection")
    entra_subject          = ""                        # Leave empty for now — filled in Step 5
  }
]
```

## Step 3 — Deploy Full Stack

Run the **Deploy Full Stack** workflow in your deployment repository. This creates:

- The managed identity (`fkh-<name>-identity-ado`)
- A Reader role assignment on the subscription (needed for ADO to verify the connection)

After the deploy completes, get the identity's Client ID from the Terraform output:

```pwsh
terraform output ado_identity_client_id
```

Or find it in the deploy workflow logs. Note this value — you'll need it in the next step.

## Step 4 — Create the Service Connection in Azure DevOps

1. Go to your Azure DevOps project → **Project Settings** → **Service connections**
2. Click **New service connection**
3. Select **Azure Resource Manager** → **Next**
4. Set:
   - **Identity type**: `App registration or managed identity (manual)`
   - **Credential**: `Workload identity federation`
5. Under **Step 1: Basics**, fill in:
   - **Service Connection Name**: Must match `devops_connection_name` from Step 2
   - **Environment**: Azure Cloud
   - **Directory (tenant) ID**: Your Azure AD tenant ID
6. Click **Next** — the **Step 2: App registration details** page appears.
   The **Issuer** and **Subject identifier** fields are pre-filled (read-only).
7. Copy the full **Subject identifier** value (starts with `/eid1/c/pub/t/...`) — you'll need it in Step 5.
8. Fill in the remaining fields:

   | Field | Value |
   |-------|-------|
   | **Scope Level** | Subscription |
   | **Subscription ID** | Your Azure subscription ID |
   | **Subscription name** | Your Azure subscription name |
   | **Application (client) ID** | The `ado_identity_client_id` from Step 3 |

9. Optionally check **"Grant access permission to all pipelines"**
10. Click **"Keep as draft"** — do NOT click "Verify and save" yet.

## Step 5 — Add the Subject Identifier and Redeploy

1. In your `config/deployment.tfvars`, paste the subject identifier into the `entra_subject` field:

   ```hcl
   allowed_ado_connections = [
     {
       devops_org_id          = "<your-org-id>"
       devops_org             = "<your-org-name>"
       devops_project         = "<your-project-name>"
       devops_connection_name = "<connection-name>"
       entra_subject          = "/eid1/c/pub/t/..."    # Paste the full subject from Step 4
     }
   ]
   ```

2. Commit and push the change.
3. Run the **Deploy Full Stack** workflow again. This creates the federated credential on the managed identity, linking it to your service connection.

## Step 6 — Verify the Service Connection

1. Go back to Azure DevOps → **Project Settings** → **Service connections**
2. Open your connection (it shows as draft) → click **Edit**
3. Click **"Verify and save"**
4. The verification should succeed. If it fails with a permission error, wait a minute for the role assignment to propagate and try again.
5. Optionally check **"Grant access permission to all pipelines"** (or grant per-pipeline later).

## Step 7 — Get the Service Connection ID

1. Open the service connection in Azure DevOps
2. The URL contains the connection ID: `.../_settings/adminservices?resourceId=<connection-id>`
3. Or find it displayed at the top of the connection page as **ID: `<guid>`**

Note this ID — you'll use it as `scId` in your pipeline.

## Step 8 — Create a Test Pipeline

Create a new pipeline in your Azure DevOps project with the following YAML to verify everything works:

```yaml
# Test Azure DevOps Connection

trigger:
- main

pool:
  vmImage: ubuntu-latest

variables:
  scId: '<connection-id-from-step-7>'

steps:
- task: AzureCLI@2
  displayName: 'Run fkh status with OIDC'
  inputs:
    azureSubscription: '<connection-name>'
    scriptType: pscore
    scriptLocation: inlineScript
    addSpnToEnvironment: true
    inlineScript: |
      dotnet tool install -g fkh --prerelease
      $env:PATH += ":$HOME/.dotnet/tools"
      fkh status --useOIDC
  env:
    DEVOPS_TOKEN: $(System.AccessToken)
    DEVOPS_REQUEST_URL: $(System.OidcRequestUri)
    DEVOPS_CONNECTION_ID: $(scId)
    FKH_BACKEND_URL: <your-function-url>
```

Replace the placeholders:

| Placeholder | Source |
|-------------|--------|
| `<connection-id-from-step-7>` | The service connection ID (GUID) from Step 7 |
| `<connection-name>` | The service connection name (must match `devops_connection_name`) |
| `<your-function-url>` | The Terraform output `function_url` (e.g. `https://fkh-myorg-backend.azurewebsites.net/api`) |

## Troubleshooting

| Error | Cause | Fix |
|-------|-------|-----|
| `AADSTS700211: No matching federated identity record` | Federated credential doesn't exist on the identity | Verify `entra_subject` is set correctly in tfvars and redeploy |
| `The subscription doesn't exist in cloud 'AzureCloud'` | Identity has no Reader role on the subscription | Redeploy (Terraform adds Reader) or assign manually |
| `Audience validation failed` | Token audience doesn't match `ADO_IDENTITY_CLIENT_ID` | Ensure the service connection's Application (client) ID matches the Terraform identity |
| `service connection not authorized` | Connection name or org ID mismatch | Verify `allowed_ado_connections` values match exactly |
| Edit button grayed out | Connection was created in "automatic" mode | Delete and recreate using "manual" mode |
