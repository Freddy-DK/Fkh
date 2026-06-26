# fkh-web

Responsive **React 19** SPA for managing Fkh containers in the browser. Third client alongside the VS Code extension and CLI; uses the same backend REST API and GitHub authentication.

## Tech stack

- **TypeScript** (strict)
- **React 19** + **Vite 6**
- ESM (`"type": "module"` in `package.json`)

Key files: `package.json`, `tsconfig.json`, `vite.config.ts`, `src/`.

## Directory structure

```
fkh-web/
├── src/
│   ├── main.tsx, App.tsx
│   ├── api.ts          # Backend HTTP calls
│   ├── auth.ts         # GitHub PAT + device flow
│   ├── types.ts
│   └── components/     # Login, ContainerList, Header, ...
├── public/             # Icons and static assets
└── dist/               # Production build output (gitignored)
```

## Build commands

```powershell
cd fkh-web
npm install
npm run dev      # http://localhost:5173
npm run build    # tsc && vite build → dist/
npm run preview  # preview production build
```

Local dev with backend:

```
http://localhost:5173/?backendUrl=https://fkh-<org>-backend.azurewebsites.net/api
```

Optional OAuth device flow:

```
http://localhost:5173/?clientId=<github-oauth-app-id>&backendUrl=...
```

The Function App must allow `http://localhost:5173` in CORS (configured in Terraform when the web app is enabled).

## Test commands

No Jest/Vitest/Playwright configured. Test manually in the browser against a dev or deployed backend.

## Backend URL resolution

Order in application code:

1. `?backendUrl=...` query parameter
2. Hostname inference (`fkh-<org>-web` → `fkh-<org>-backend`)
3. `http://localhost:7071/api` on localhost

## Deploy

CI sets `VITE_GITHUB_CLIENT_ID` and `VITE_BACKEND_URL`, then deploys `dist/` via Azure Static Web Apps CLI (`DeployFkhFullStack.yml` / `UpdateFkhBackEnd.yml` when `enable_web_app` is true in tfvars).

## Architecture patterns

- **Bearer token** auth: `Authorization: Bearer <github-token>` — same as CLI/VSIX.
- API layer centralized in `api.ts`; auth in `auth.ts`.
- Functional React components; keep side effects in hooks or explicit handlers.
- PWA manifest may be generated in `vite.config.ts` using `VITE_BACKEND_URL`.

## Coding conventions

- `tsconfig.json`: `strict`, `noUnusedLocals`, `noUnusedParameters`, `noUncheckedIndexedAccess`.
- Prefer explicit types for API responses in `types.ts`.
- No repo-level ESLint/Prettier — match existing formatting in touched files.
- Do not store tokens in localStorage patterns that differ from existing `auth.ts` without a security review.

## When changing the API

Coordinate with `fkh-backend` and protocol version in root `SupportedClientVersions.json` if contracts change. CLI/VSIX may not need changes if they only use catalog endpoints you mirror in the web UI.

## Related

- [fkh-backend/AGENTS.md](../fkh-backend/AGENTS.md)
- [terraform/AGENTS.md](../terraform/AGENTS.md) — `webapp.tf`, CORS locals
