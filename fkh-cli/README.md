# Freddy's Kubernetes Helper

Command-line client for managing Business Central containers hosted in a Kubernetes cluster with a persisted SQL Server on Linux.

Fkh is also available as a VS Code extension: [Freddy's Kubernetes Helper on the Marketplace](https://marketplace.visualstudio.com/items?itemName=Freddy-DK.fkh).

## Getting started

1. Deploy the Fkh Backend (see [Fkh home](https://github.com/Freddy-DK/Fkh))
2. Install the CLI from NuGet, using:

```bash
dotnet tool install --global fkh
```

## Configuration

The CLI looks for the backend URL in this order:

1. `--backendUrl <url>` command-line option
2. `FKH_BACKEND_URL` environment variable
3. `~/.fkh/settings.json` (recommended for global tool installs)
4. `fkh.settings.json` next to the executable

Create a settings file with:

```json
{
    "backendUrl": "https://fkh-<org>-backend.azurewebsites.net/api"
}
```

## Authentication

Authentication is resolved in this order:

1. `--useOIDC` — Fetch OIDC token from GitHub Actions environment (auto-refreshes)
2. `GH_TOKEN` environment variable
3. `gh auth token` — GitHub CLI (interactive fallback). Use `--ghUser <user>` to target a specific GitHub account.

### Access Control

Access to the backend is guarded by two GitHub teams configured in your organization:

- **Members team** — grants usage access. Members can create, manage, and remove their own containers and images.
- **Admins team** — grants admin access. Admins can view all containers, list cluster nodes, and use admin-only commands like `listcontainers --all` and `listvms`.

In addition, **GitHub repositories** can be granted usage access via OIDC. Repositories listed in the `allowed_oidc_repos` configuration can authenticate from GitHub Actions workflows without any stored secrets. Containers created by a repository are owned by that repository.

For GitHub Actions workflows using OIDC, see [Using OIDC in workflows](#using-oidc-in-workflows).

## Usage

```
fkh <command> [--key "value" ...]
```

### Global Options

| Option | Description |
|--------|-------------|
| `--useOIDC` | Fetch OIDC token from GitHub Actions (auto-refreshes) |
| `--ghUser <user>` | GitHub user account for `gh auth token -u <user>` |
| `--backendUrl <url>` | Override the backend URL (takes priority over env/settings) |
| `--nowait` | Don't wait for completion (applies to `createcontainer`, `createimage`) |
| `--asJson` | Output the result as JSON |
| `-h`, `--help` | Show help and list available commands |
| `--version` | Show version |

## Commands

### createcontainer

Creates a new Business Central container with a persisted SQL Server database.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | Yes | Name of the container |
| `artifactUrl` | string | Yes | BC artifact URL or shorthand (e.g. `///us/latest`) |
| `adminUsername` | string | Yes | Admin username for the BC instance |
| `adminPassword` | string | Yes | Admin password for the BC instance |
| `useDatabase` | string | No | Database to restore. A SAS URL (https://...) or 'name/version' referencing an uploaded database (use 'latest' for most recent) |
| `autostop` | string | No | Auto-stop time (e.g. `4h` or `18:00`) |
| `cpu` | string | No | CPU request (default: `500m`) |
| `memory` | string | No | Memory limit (default: `3Gi`) |
| `repo` | string | No | Repository name to tag the container with |
| `project` | string | No | AL-Go project name to tag the container with |
| `spot` | boolean | No | Use spot node (default: `false`) |

```bash
fkh createcontainer --name mybc --artifactUrl "///us/latest" --adminUsername admin --adminPassword "P@ssw0rd"
```

### removecontainer

Removes a container and its database permanently.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | Yes | Name of the container to remove |

```bash
fkh removecontainer --name mybc
```

### startcontainer

Starts a stopped container by scaling it to 1 replica.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | Yes | Name of the container to start |

### stopcontainer

Stops a running container by scaling it to 0 replicas. The database is preserved.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | Yes | Name of the container to stop |

### extendautostop

Extends the auto-stop timer by 2 hours.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | Yes | Name of the container |

### listcontainers

Lists your containers with status, image, auto-stop time, and memory usage.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `all` | boolean | No | Show all containers (admin only) |

```bash
fkh listcontainers
fkh listcontainers --asJson
```

### allowsqlaccess

Opens external SQL Server access for a specific IP address.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `ip` | string | Yes | IP address to allow (auto-detected if not provided) |
| `hours` | string | No | Number of hours to allow access (default: `2`) |
| `mySqlPassword` | string | No | Custom SQL password |

### revokesqlaccess

Revokes external SQL Server access immediately.

### createimage

Builds a Business Central container image in Azure Container Registry.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| *(discovered dynamically)* | | | |

```bash
fkh createimage --nowait
```

### removeimage

Removes an image or tag from the Azure Container Registry.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `repository` | string | Yes | Repository name |
| `tag` | string | No | Specific tag to remove (omit to remove entire repository) |

### listimages

Lists available images and tags in the Azure Container Registry.

### listvms

Lists Kubernetes cluster nodes with status, CPU, memory, and container assignments. Admin only.

### getcontainerlogs

Retrieves logs from a container.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | string | Yes | Name of the container |

```bash
fkh getcontainerlogs --name mybc
```

### invokesqlcmd

Executes a SQL statement against a container's database.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| *(discovered dynamically)* | | | |

## Using OIDC in Workflows

Repositories configured in `allowed_oidc_repos` can authenticate without storing secrets:

```yaml
permissions:
  id-token: write

steps:
  - name: Create container
    run: fkh createcontainer --name mybc --artifactUrl "///us/latest" --useOIDC
```
