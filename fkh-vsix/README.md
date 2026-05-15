# Freddy's Kubernetes Helper

Provision and manage Business Central containers hosted in your own Kubernetes cluster, with a persisted SQL Server running on Linux as the database server. All access is managed through a backend Azure Function — authentication is handled via your GitHub login.

Fkh is also available as a command-line interface: [fkh on NuGet](https://www.nuget.org/packages/fkh).

## Getting Started

**1** Deploy the Fkh Backend (see [Fkh home](https://github.com/Freddy-DK/Fkh)).

**2** Install the extension from the VS Code Marketplace.

**3** Set the backend URL in your settings: **Fkh: Backend Url** (e.g. `https://fkh-<org>-backend.azurewebsites.net/api`).

**4** The extension will prompt you to sign in with GitHub on first use.

## Authentication & Access Control

The extension authenticates you via GitHub OAuth. When you first interact with the backend, VS Code will prompt you to sign in with your GitHub account.

Access to the backend is guarded by two GitHub teams configured in your organization:

**Members team** — grants usage access. Members can create, manage, and remove their own containers and images.

**Admins team** — grants admin access. Admins can view all containers, list cluster nodes, and use admin-only features like the VMs view.

## Activity Bar

The extension adds an **Fkh** panel to the activity bar with the following tree views:

### AL-Go Projects

If your workspace contains an [AL-Go](https://github.com/microsoft/AL-Go) repository, this view automatically discovers all AL-Go projects in the repo and displays them as top-level nodes. For each project you can:

**Create a container** directly from the project node — the container is automatically wired to the correct repository, project, and artifact settings.

**See existing containers** nested under each project, with live status indicators (Running, Starting, Stopped, etc.).

**Start, stop, extend auto-stop, view logs, or remove** containers using inline actions or the context menu.

Containers created for a project are tagged with the repository and project name, so the extension always shows you exactly which containers belong to which project.

### Containers

A flat list of **all your containers** across all repositories and projects. Each container can be expanded to see its properties:

**Status** — Running, Starting, Stopped, Pending, Initializing

**Image** — The Business Central artifact image the container is built from

**Auto-Stop** — When the container will automatically stop (displayed in your local timezone)

**Repo / Project** — Which repository and AL-Go project the container belongs to

**Web Client** — A clickable link to open the Business Central web client

**Memory** — Current memory usage

All the same actions (start, stop, extend auto-stop, get logs, remove) are available here as well.

### Images

Lists your container images stored in the Azure Container Registry. You can create new images from Business Central artifacts or remove existing image tags.

### VMs (Admin only)

Visible only to administrators. Shows the Kubernetes cluster nodes with their status, CPU, memory, and which containers are running on each node.

## Commands

Open the command palette and type **Fkh** to see all available commands:

| Command | Description |
|---------|-------------|
| **Fkh: Run Command** | Browse and run any backend function from a dynamic catalog. These are the same functions available in the [fkh CLI](https://www.nuget.org/packages/fkh). |
| **Fkh: Create Container** | Create a new container with a guided parameter form |
| **Fkh: Create Image** | Create a new container image from Business Central artifacts |

## Settings

| Setting | Description |
|---------|-------------|
| `fkh.backendUrl` | URL of the Fkh backend Azure Function |
| `fkh.timezone` | IANA timezone override (e.g. `Europe/Copenhagen`). Useful when running in Codespaces or remote environments where auto-detection returns UTC. |

You can also pre-fill function parameters as settings (e.g. `fkh.CreateContainer.artifactUrl`) to skip prompts for values you use frequently.

## Development

### Prerequisites

- [Node.js](https://nodejs.org/) (LTS recommended)
- VS Code

### Setup

```powershell
cd fkh-vsix
npm install
```

### Build

```powershell
npm run build
```

### Run / Debug

Press **F5** in VS Code to launch an Extension Development Host with the extension loaded.
