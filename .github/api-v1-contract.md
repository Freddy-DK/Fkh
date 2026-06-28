# Fkh Backend — API v1 Contract

## Base URL

All endpoints are hosted as Azure Functions. The base URL is configured per-environment (e.g. `https://<functionapp>.azurewebsites.net/api/`).

## Authentication

Every endpoint (except the catalog) requires a GitHub token in the `Authorization` header:

```
Authorization: Bearer <github-token>
```

Supported token types:
- **GitHub Personal Access Token (PAT)** — validated via the GitHub API; org/team membership checked against `ALLOWED_ORG_TEAMS` / `ADMIN_ORG_TEAMS` / `SUPPORT_ORG_TEAMS`, or matched directly via `ALLOWED_USERS`
- **GitHub OIDC token** — for CI/CD (GitHub Actions); repository validated against `ALLOWED_OIDC_REPOS`

### Brute-force protection

3 failed authentication attempts from the same IP within 5 minutes results in a temporary block. Blocked requests receive `403 Forbidden`.

## Common response patterns

### Success — `200 OK`

```
Content-Type: application/json; charset=utf-8
```

Body is the JSON-serialized result of the operation. Property names use **camelCase**; null properties are omitted.

### Long-running operation — `202 Accepted`

Returned when the service throws a `RetryAfterException`. The client should poll the same endpoint after the indicated delay.

```
Retry-After: <seconds>
```

```json
{
  "message": "Container is being created...",
  "retryAfterSeconds": 10
}
```

### Validation error — `400 Bad Request`

Plain-text body describing the issue (e.g. `"Missing required parameter 'name' for CreateContainer."`).

### Auth errors

| Status | Meaning |
|---|---|
| `401 Unauthorized` | Missing or malformed `Authorization` header |
| `403 Forbidden` | Token valid but user not in an allowed team, IP blocked, or admin-only function called by non-admin |

### Cluster unavailable — `503 Service Unavailable`

Returned when the AKS cluster is stopped or unreachable.

### Server error — `500 Internal Server Error`

Plain-text body with exception type, message, and a short stack trace.

---

## Discovery endpoint

### `GET /functions`

Returns the function catalog — the list of all non-hidden functions, their parameters, types, defaults, and descriptions. **No authentication required.**

**Response** — `200 OK`:

```json
{
  "functions": [
    {
      "name": "CreateContainer",
      "description": "Creates a container using the provided artifact and admin credentials.",
      "route": "CreateContainer",
      "adminOnly": false,
      "parameters": [
        {
          "name": "name",
          "type": "string",
          "description": "Name for the container.",
          "required": true,
          "adminOnly": false,
          "defaultValue": null
        }
      ]
    }
  ]
}
```

---

## Invoking functions

All catalog functions accept **`POST /<Route>`** with a JSON body:

```
Content-Type: application/json
```

```json
{
  "parameters": {
    "name": "mycontainer",
    "artifactUrl": "https://..."
  }
}
```

Functions that accept file-type parameters use **`POST /<Route>`** with `multipart/form-data`. The JSON parameters are sent in a form field named `parameters`, and each file is sent as a separate form part keyed by the parameter name.

### Parameter validation rules

- Unknown parameter names are rejected (`400`).
- Missing required parameters are rejected (`400`).
- The `name` parameter (when present) is restricted to alphanumeric characters for regular users; admins may also use hyphens.
- Default values from the catalog are applied server-side when a parameter is omitted.

---

## Function catalog

### Container lifecycle

| Function | Route | Method | Admin | Description |
|---|---|---|---|---|
| CreateContainer | `POST /CreateContainer` | JSON | No | Creates a BC container with the given artifact, credentials, and options |
| RemoveContainer | `POST /RemoveContainer` | JSON | No | Removes a container and its database |
| StartContainer | `POST /StartContainer` | JSON | No | Starts a stopped container (scales deployment to 1 replica) |
| StopContainer | `POST /StopContainer` | JSON | No | Stops a container (scales deployment to 0 replicas) |
| StopAllContainers | `POST /StopAllContainers` | JSON | **Yes** | Stops all running containers in the cluster |
| ListContainers | `POST /ListContainers` | JSON | No | Lists containers (own by default; set `all=true` for all) |
| WaitForContainer | `POST /WaitForContainer` | JSON | No | Polls until the container is running and ready |

### Auto-stop

| Function | Route | Method | Admin | Description |
|---|---|---|---|---|
| SetAutoStop | `POST /SetAutoStop` | JSON | No | Sets the auto-stop time (`<n>h` or time of day) |
| ExtendAutoStop | `POST /ExtendAutoStop` | JSON | No | Extends auto-stop by 2 hours |
| ClearAutoStop | `POST /ClearAutoStop` | JSON | **Yes** | Clears the auto-stop time |

### Container operations

| Function | Route | Method | Admin | Description |
|---|---|---|---|---|
| PublishApp | `POST /PublishApp` | multipart | No | Publishes a `.app` file to a running container |
| InvokeSqlCmd | `POST /InvokeSqlCmd` | JSON | No | Executes a SQL statement against a container's database |
| InvokeScript | `POST /InvokeScript` | JSON or multipart | No | Runs a PowerShell script inside a container (inline or file) |
| GetContainerLog | `POST /GetContainerLog` | JSON | No | Returns container logs (tail N lines) |
| GetContainerEventLog | `POST /GetContainerEventLog` | JSON | No | Downloads the Windows Application event log (.evtx) |
| CopyFileFromContainer | `POST /CopyFileFromContainer` | JSON | No | Downloads a file from a container (wildcard supported) |
| CopyFileToContainer | `POST /CopyFileToContainer` | multipart | No | Uploads a file to a container |
| BackupTenantDatabase | `POST /BackupTenantDatabase` | JSON | No | Backs up a tenant database to blob storage |

### SQL access

| Function | Route | Method | Admin | Description |
|---|---|---|---|---|
| AllowSqlAccess | `POST /AllowSqlAccess` | JSON | No | Opens external SQL access for your IP (temporary LoadBalancer + network policy) |
| RevokeSqlAccess | `POST /RevokeSqlAccess` | JSON | No | Revokes external SQL access immediately |

### Images (ACR)

| Function | Route | Method | Admin | Description |
|---|---|---|---|---|
| CreateImage | `POST /CreateImage` | JSON | No | Builds a BC image in ACR from an artifact URL |
| RemoveImage | `POST /RemoveImage` | JSON | No | Removes an image tag or repository from ACR |
| ListImages | `POST /ListImages` | JSON | No | Lists available images in ACR |

### Pre-pull (admin)

| Function | Route | Method | Admin | Description |
|---|---|---|---|---|
| ListPrepulled | `POST /ListPrepulled` | JSON | **Yes** | Lists images configured for pre-pulling |
| AddPrepull | `POST /AddPrepull` | JSON | **Yes** | Adds an image to the pre-pull list |
| RemovePrepull | `POST /RemovePrepull` | JSON | **Yes** | Removes an image from the pre-pull list |

### Settings

| Function | Route | Method | Admin | Description |
|---|---|---|---|---|
| GetSettings | `POST /GetSettings` | JSON | No | Gets user settings (admins can view any user) |
| SetSettings | `POST /SetSettings` | JSON | No | Sets a user setting (admins can set for any user, `_members`, `_admins`) |
| ClearSettings | `POST /ClearSettings` | JSON | **Yes** | Clears settings for a user |

### Current user

| Function | Route | Method | Admin | Description |
|---|---|---|---|---|
| GetCurrentUser | `POST /GetCurrentUser` | JSON | No | Returns the authenticated Fkh username and authorization flags |

### VMs

| Function | Route | Method | Admin | Description |
|---|---|---|---|---|
| ListVMs | `POST /ListVMs` | JSON | **Yes** | Lists Windows VMs (nodes) in the cluster |

### Cluster management (admin)

| Function | Route | Method | Admin | Description |
|---|---|---|---|---|
| StartFkh | `POST /StartFkh` | JSON | **Yes** | Starts a stopped AKS cluster |
| StopFkh | `POST /StopFkh` | JSON | **Yes** | Stops the AKS cluster to save costs |

### Hidden endpoints

These are not returned by the catalog but can be invoked directly by clients that know the route.

| Function | Route | Admin | Description |
|---|---|---|---|
| GetDatabaseUploadSas | `POST /GetDatabaseUploadSas` | **Yes** | Returns a SAS URL for uploading database backups |
| GetDatabaseDownloadSas | `POST /GetDatabaseDownloadSas` | No | Returns a read-only SAS URL for downloading database backups |
| Status | `POST /Status` | **Yes** | Returns full system status (nodes, containers, SQL, storage, quotas, security) |

---

## Detailed parameter reference

### CreateContainer

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `name` | string | **yes** | — | Container name (combined with GitHub username) |
| `artifactUrl` | string | **yes** | — | BC artifact URL |
| `adminUsername` | string | **yes** | — | Admin username for the container |
| `adminPassword` | string | **yes** | — | Admin password for the container |
| `useDatabase` | string | no | — | Database to restore (SAS URL or `name/version`, use `latest` for newest) |
| `tenantDatabase` | string | no | — | Tenant database to restore (makes container multitenant) |
| `autostop` | string | no | — | Auto-stop time (`<n>h` or time of day in UTC) |
| `cpu` | string | no | `250m` | CPU request (e.g. `250m`, `1`, `2`) |
| `memory` | string | no | `3Gi` | Memory request (e.g. `3Gi`, `8Gi`) |
| `repo` | string | no | — | Source repository metadata (`org/repo`) |
| `project` | string | no | — | AL-Go project name metadata |
| `moveAllAppsToDevScope` | boolean | no | `false` | Move published apps to dev scope after DB restore |
| `multitenant` | boolean | no | `false` | Create as multitenant |
| `spot` | boolean | no | `false` | Place on a Spot (preemptible) VM |
| `authenticationEmail` | string | no | — | Email for Azure AD authentication |

### RemoveContainer

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `name` | string | **yes** | — | Container name |

### StartContainer

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `name` | string | **yes** | — | Container name |
| `autostop` | string | no | — | Auto-stop time |

### StopContainer

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `name` | string | **yes** | — | Container name |

### ListContainers

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `all` | boolean | no | — | List all containers instead of only your own |

### SetAutoStop

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `name` | string | **yes** | — | Container name |
| `autostop` | string | **yes** | — | Auto-stop time (`<n>h` or time of day) |

### ExtendAutoStop

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `name` | string | **yes** | — | Container name |

### ClearAutoStop

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `name` | string | **yes** | — | Container name |

### WaitForContainer

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `name` | string | **yes** | — | Container name |

### AllowSqlAccess

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `ip` | string | **yes** | — | Your public IP address |
| `hours` | string | no | `2` | Hours to keep SQL access open |
| `mySqlPassword` | string | no | — | If set, creates a SQL login with this password |

### RevokeSqlAccess

No parameters.

### CreateImage

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `artifactUrl` | string | **yes** | — | Artifact URL to build from |

### RemoveImage

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `repository` | string | **yes** | — | Repository name in ACR |
| `tag` | string | no | — | Tag to remove (omit to remove entire repository) |

### ListImages

No parameters.

### ListVMs

No parameters.

### PublishApp

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `name` | string | **yes** | — | Container name |
| `appFile` | file | **yes** | — | The `.app` file to publish |
| `syncMode` | string | no | `Add` | Sync mode (`Add`, `ForceSync`, `Clean`, `Development`) |

### InvokeSqlCmd

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `name` | string | **yes** | — | Container name |
| `query` | string | **yes** | — | SQL statement to execute |

### InvokeScript

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `name` | string | **yes** | — | Container name |
| `command` | string | no | — | Inline PowerShell script (use this or `scriptFile`) |
| `scriptFile` | file | no | — | PowerShell script file (use this or `command`) |

### GetContainerLog

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `name` | string | **yes** | — | Container name |
| `tail` | string | no | `500` | Number of log lines from the end |
| `previous` | boolean | no | `false` | Get logs from the previous (crashed) instance |

### GetContainerEventLog

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `name` | string | **yes** | — | Container name |

### CopyFileFromContainer

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `name` | string | **yes** | — | Container name |
| `containerFilename` | string | **yes** | — | Path inside the container (wildcards supported) |

### CopyFileToContainer

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `name` | string | **yes** | — | Container name |
| `containerFilename` | string | **yes** | — | Destination path inside the container |
| `file` | file | **yes** | — | File to upload |

### BackupTenantDatabase

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `name` | string | **yes** | — | Container name |
| `tenant` | string | no | `default` | Tenant to back up |
| `backupName` | string | **yes** | — | Backup name (folder in blob storage) |
| `backupVersion` | string | **yes** | — | Backup version (blob name) |

### GetSettings

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `username` | string | no | — | Username (omit for own settings; admins can query any user) |
| `property` | string | no | — | Specific setting to retrieve |

### SetSettings

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `username` | string | no | — | Username (omit for own; admins can use `_members` / `_admins`) |
| `property` | string | **yes** | — | Setting name |
| `value` | string | **yes** | — | Setting value |

### ClearSettings

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `username` | string | **yes** | — | Username (or `_members` / `_admins`) |
| `property` | string | no | — | Setting to remove (omit to clear all) |

### ListPrepulled

No parameters.

### AddPrepull

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `image` | string | **yes** | — | Full image reference to pre-pull |

### RemovePrepull

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `image` | string | **yes** | — | Full image reference to remove |

### GetDatabaseUploadSas (hidden)

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `containerName` | string | no | `databases` | Blob container name |

### GetDatabaseDownloadSas (hidden)

No parameters.

### Status (hidden)

No parameters.

### GetCurrentUser

No parameters.

Response:

```json
{
  "username": "octocat",
  "isAdmin": true,
  "isSupport": false
}
```

### StopAllContainers

No parameters.

### StartFkh

No parameters.

### StopFkh

No parameters.
