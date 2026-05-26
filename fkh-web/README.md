# Fkh Web

A responsive web app for managing Business Central containers on Fkh. Works on phones, tablets, and desktop browsers.

## Features

- **GitHub authentication** — sign in with a Personal Access Token or GitHub OAuth device flow
- **Container list** — see all your containers with live status (Running, Stopped, Pending, etc.)
- **Expand for details** — image, auto-stop, repo, project, web client link, memory usage
- **Start / Stop** — control containers directly from the browser
- **Responsive** — optimized for mobile, tablet, and desktop

## Development

### Prerequisites

- [Node.js](https://nodejs.org/) (LTS recommended)

### Setup

```powershell
cd fkh-web
npm install
```

### Run locally

```powershell
npm run dev
```

Opens a dev server at `http://localhost:5173`. Pass the backend URL as a query parameter:

```
http://localhost:5173/?backendUrl=https://fkh-<org>-backend.azurewebsites.net/api
```

> **Note:** The Azure Function App must have `http://localhost:5173` in its CORS allowed origins for local development.

### Build for production

```powershell
npm run build
```

Output is written to `dist/`.

## Backend URL Resolution

The web app resolves the backend URL in this order:

1. `?backendUrl=...` query parameter
2. Inferred from hostname — if hosted at `fkh-<org>-web.azurewebsites.net`, the backend is assumed to be `fkh-<org>-backend.azurewebsites.net/api`
3. `http://localhost:7071/api` when running on localhost (for local Azure Functions development)

## Authentication

Two sign-in methods are supported:

- **GitHub OAuth Device Flow** — add `?clientId=<your-oauth-app-client-id>` to the URL. Requires a GitHub OAuth App with device flow enabled.
- **Personal Access Token** — paste a GitHub PAT with `read:user` and `read:org` scopes. Available as a fallback when no Client ID is configured.

The token is sent to the backend as `Authorization: Bearer <token>`, the same format used by the CLI and VS Code extension.

### Setting up GitHub OAuth (Device Flow)

To enable the "Sign in with GitHub" button, you need a GitHub OAuth App:

1. Go to **GitHub** → **Settings** → **Developer settings** → **OAuth Apps** → **New OAuth App**
2. Fill in the form:
   - **Application name**: `Fkh Web` (or any name you prefer)
   - **Homepage URL**: the URL where the web app is hosted (e.g. `https://fkh-<org>-web.azurewebsites.net`)
   - **Authorization callback URL**: `http://localhost` (not used by the device flow, but the field is required)
3. Click **Register application**
4. On the app's settings page, scroll down to **Device Flow** and **enable** it
5. Copy the **Client ID** (you do not need a client secret — the device flow is a public client)

Pass the Client ID to the web app via query parameter:

```
https://fkh-<org>-web.azurewebsites.net/?clientId=Ov23li...
```

Or for local development:

```
http://localhost:5173/?clientId=Ov23li...&backendUrl=https://fkh-<org>-backend.azurewebsites.net/api
```

> **Tip:** The OAuth App should be created under your GitHub **organization** (not a personal account) so it appears trusted to your users. Go to your org's settings → Developer settings → OAuth Apps.
