# AAD Authentication for Containers

By default, containers use **NavUserPassword** authentication (username + password). You can optionally enable **Azure AD (AAD) authentication** so users sign in with their Microsoft 365 / Entra ID accounts.

## Overview

AAD authentication works out of the box — no manual App Registration setup required. When a container is created with `authenticationEmail`, the Function automatically:

1. Creates a dedicated AAD App Registration for that container
2. Configures the correct redirect URI and token settings
3. Passes the AAD app details to the container as environment variables
4. Deletes the App Registration when the container is removed

The managed identity authenticates to the Microsoft Graph API using `Application.ReadWrite.OwnedBy`, which limits it to managing only apps it created.

---

## Using AAD Authentication

Pass `authenticationEmail` when creating a container:

- **VS Code extension**: Set the `authenticationEmail` parameter to your email address
- **CLI**: `fkh create --authenticationEmail user@contoso.com ...`
- **API**: Include `"authenticationEmail": "user@contoso.com"` in the request body

The container's web client URL will use the `/BC/SignIn` path for AAD login instead of the default username/password login.

---

## How It Works

1. When `authenticationEmail` is provided, the Function creates a new AAD App Registration named `[<prefix>]fkh-<container>-auth` with a redirect URI of `https://<container-fqdn>/BC/SignIn`. The optional prefix is controlled by the `aad_app_name_prefix` setting in `deployment.tfvars`.
2. The app's client ID is stored as a Kubernetes annotation (`fkh/aad-app-object-id`) on the deployment so it can be cleaned up later.
3. The container receives `AadAppId`, `AadAppRedirectUri`, `AadTenantId`, and `authenticationEMail` as environment variables.
4. Users sign in via the standard Microsoft login flow in the browser.
5. When the container is removed, the Function deletes the AAD App Registration automatically.

---

## Running tests

`fkh runtests` works on AAD containers the same way as on NavUserPassword containers. No password or token is passed by the caller, and `--authenticationEmail` is not used for the test session.

Business Central refuses app-only (service-to-service) tokens for the UI client session that the test runner needs. So on AAD containers the test worker runs the tests through a second, internal service tier:

1. On the first `runtests`, [`Initialize-FkhTestInstance.ps1`](ContainerScripts/Initialize-FkhTestInstance.ps1) creates the service tier `FkhTest` on the same database. It copies the main tier's settings and changes `ClientServicesCredentialType` to `NavUserPassword`, moves the ports to 7145–7149, and turns off the task scheduler and web services.
2. A web client for `FkhTest` is published on `http://127.0.0.1:8181/`. It is bound to localhost only and is not exposed by the Kubernetes service.
3. The tests sign in there as the container admin user (`username`/`password` env vars), exactly like on NavUserPassword containers.

The test tier keeps running for later runs until the pod restarts, and it uses extra memory while it runs. The first run on a container takes longer while the tier starts.

To hide that start time, start the tier in the background as soon as the container is ready, then publish apps as usual:

```powershell
fkh waitforcontainer --name $c --useOIDC
fkh invokescript --name $c --nowait --useOIDC --command "powershell.exe -NoProfile -File C:\run\my\Initialize-FkhTestInstance.ps1"
# publish apps ...
fkh runtests --name $c --extensionId $id --useOIDC
```

`runtests` waits for the tier if it is still starting. To free the memory without a restart, run the same `invokescript` command with `-Remove` appended.

AAD containers created before this change need a restart (`stopcontainer`/`startcontainer`) so they download the updated container scripts. No backend change is required.

---

## Prerequisites

The Function's managed identity needs the `Application.ReadWrite.OwnedBy` Microsoft Graph permission. This is granted automatically by Terraform during deployment — no manual steps required.

---

## Troubleshooting

| Error | Cause | Fix |
|-------|-------|-----|
| "Failed to create AAD App Registration" | Managed identity lacks Graph permissions | Re-run `terraform apply` to ensure the `Application.ReadWrite.OwnedBy` role is assigned |
| "Insufficient privileges to complete the operation" | Same as above | Check the managed identity's Graph API permissions in Azure Portal → Enterprise Applications |
| "Container test execution does not support 'AccessControlService' client authentication" | The container still runs the old container scripts | Restart the container |
| "Tenant '...' is not operational on the 'FkhTest' test service tier" | The test tier did not start within 5 minutes | Check `GetContainerEventLog`; retry `runtests` |
