# fkh-vsix

**VS Code extension** for managing Business Central containers on Fkh. Catalog-driven command palette, tree views (AL-Go projects, containers, images, VMs), and GitHub OAuth via VS Code authentication API.

## Tech stack

- **TypeScript** (strict)
- **VS Code Extension API** (`engines.vscode ^1.85.0`)
- **esbuild** — bundles `src/extension.ts` (Node + browser targets)
- **@vscode/vsce** — package and publish

Shipping entry: `dist/extension.js` (Node), `dist/web/extension.js` (web/Codespaces).

## Directory structure

```
fkh-vsix/
├── package.json            # contributes: commands, views, configuration
├── tsconfig.json
├── src/
│   ├── extension.ts        # Activation, trees, catalog runner
│   ├── protocol.ts         # PROTOCOL_VERSION, CLIENT_APP headers
│   ├── containersTreeProvider.ts
│   ├── readALGoSettings.ts
│   └── updateLaunchJson.ts
├── dist/                   # esbuild output (not tsc outDir for shipping)
└── images/
```

## Build commands

```powershell
cd fkh-vsix
npm install
npm run build          # build:node + build:web
npm run build:node     # esbuild → dist/extension.js
npm run build:web      # esbuild → dist/web/extension.js
npm run compile        # tsc -p ./ (typecheck)
npm run watch          # watch mode for development
npx vsce package
```

**F5** in VS Code launches Extension Development Host (requires `npm run build` first).

CI: `.github/workflows/DeployFkhClients.yml` — `npm ci`, build both targets, `vsce package` / publish.

## Test commands

No test runner configured. Manual test in Extension Development Host against a real or local backend.

## Settings (user/workspace)

| Setting | Purpose |
|---------|---------|
| `fkh.backendUrl` | Backend Azure Function base URL (`.../api`) |
| `fkh.timezone` | IANA timezone override for auto-stop display |
| `fkh.CreateContainer.*` | Pre-fill catalog parameters |

## Architecture patterns

### Catalog-driven commands

- Fetch `GET /api/functions` at runtime; **Fkh: Run Command** lists all backend operations.
- Do not duplicate parameter definitions — use catalog metadata for prompts (file pickers, admin-only hiding).

### Tree views

- **AL-Go Projects** — discovers projects from workspace AL-Go settings; nested containers per project.
- **Containers** — flat list of user's containers.
- **Images** — ACR repositories/tags.
- **VMs** — admin-only cluster nodes.

### Dual bundle

- **Node** bundle for desktop VS Code.
- **Web** bundle for github.dev / Codespaces (`extensionKind`: workspace + ui).

### Protocol

- `protocol.ts`: `PROTOCOL_VERSION`, `CLIENT_APP = 'VS Code extension'`.
- Keep in sync with root `SupportedClientVersions.json`.

### launch.json integration

- After container create, optionally update `.vscode/launch.json` per `fkh.CreateContainer.*` settings.

## Coding conventions

- Strict TypeScript; shipping code is **esbuild output**, not raw `tsc` emit.
- Command IDs and settings use `fkh.` prefix in `package.json` contributes.
- Register new UI only when catalog alone is insufficient (tree actions, webviews, launch.json).
- Match existing patterns in `extension.ts` for HTTP calls and error display.

## Publish

- **VS Code Marketplace** — `npx vsce publish` (requires `VSCE_PAT` in CI).
- **Open VSX** (Cursor and other Open VSX–based editors) — `npx ovsx publish <file>.vsix` (requires `OVSX_PAT` in CI). Namespace must match `publisher` in `package.json` (`Freddy-DK`).

```powershell
npx vsce publish --pre-release   # requires VSCE_PAT in CI
```

CI (`.github/workflows/DeployFkhClients.yml`) publishes to both registries when the corresponding secrets are set.

## Related

- [fkh-backend/AGENTS.md](../fkh-backend/AGENTS.md)
- [fkh-cli/AGENTS.md](../fkh-cli/AGENTS.md) — parallel client behavior
