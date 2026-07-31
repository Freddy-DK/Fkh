import * as vscode from 'vscode';
import { getGitRootUri } from './readALGoSettings';

/**
 * After a container is created, update launch.json files in app folders
 * with a configuration pointing to the new container.
 *
 * The setting `fkh.CreateContainer.updateLaunchJson` controls the scope:
 *   - "none"    — do nothing
 *   - "project" — search from repo root + project name
 *   - "repo"    — search from the repo root
 */
export async function updateLaunchJsonAfterCreate(
  containerName: string,
  serverUrl: string,
  project: string,
  alGoOverrides?: Record<string, string>,
): Promise<void> {
  const mode = vscode.workspace.getConfiguration('fkh').get<string>('CreateContainer.updateLaunchJson', 'project');
  if (mode === 'none') { return; }

  const repoRoot = getGitRootUri();
  if (!repoRoot) { return; }

  const searchRoot = mode === 'project' && project && project !== '.'
    ? vscode.Uri.joinPath(repoRoot, project)
    : repoRoot;

  const appFolders = await findAppFolders(searchRoot);
  if (appFolders.length === 0) { return; }

  for (const appFolder of appFolders) {
    await upsertLaunchConfiguration(appFolder, containerName, serverUrl, alGoOverrides);
  }
}

/**
 * After a container is removed, remove the matching launch configuration(s)
 * from launch.json files in app folders.
 *
 * The launch configuration created by `updateLaunchJsonAfterCreate` is named
 * after the container's deployment name (e.g. `myapp-deployment`), while the
 * container's app label is the base name (e.g. `myapp`). Both variants are
 * matched to be robust.
 *
 * The setting `fkh.CreateContainer.updateLaunchJson` controls the search scope,
 * mirroring the create behavior.
 */
export async function removeLaunchJsonAfterRemove(
  containerName: string,
  project?: string,
): Promise<void> {
  const mode = vscode.workspace.getConfiguration('fkh').get<string>('CreateContainer.updateLaunchJson', 'project');
  if (mode === 'none') { return; }

  const repoRoot = getGitRootUri();
  if (!repoRoot) { return; }

  const searchRoot = mode === 'project' && project && project !== '.'
    ? vscode.Uri.joinPath(repoRoot, project)
    : repoRoot;

  const appFolders = await findAppFolders(searchRoot);
  if (appFolders.length === 0) { return; }

  const names = new Set([containerName, `${containerName}-deployment`]);
  for (const appFolder of appFolders) {
    await removeLaunchConfiguration(appFolder, names);
  }
}

async function removeLaunchConfiguration(appFolder: vscode.Uri, names: Set<string>): Promise<void> {
  const launchUri = vscode.Uri.joinPath(appFolder, '.vscode', 'launch.json');

  let launchJson: { version: string; configurations: Record<string, unknown>[] };

  try {
    const content = await vscode.workspace.fs.readFile(launchUri);
    const text = new TextDecoder().decode(content);
    launchJson = JSON.parse(stripJsonComments(text));
  } catch {
    // File doesn't exist or is not parseable — nothing to clean up
    return;
  }

  if (!Array.isArray(launchJson.configurations)) { return; }

  const filtered = launchJson.configurations.filter(
    (c) => !(typeof c === 'object' && c !== null && names.has((c as Record<string, unknown>)['name'] as string)),
  );

  if (filtered.length === launchJson.configurations.length) { return; }

  launchJson.configurations = filtered;
  const output = JSON.stringify(launchJson, null, 4) + '\n';
  await vscode.workspace.fs.writeFile(launchUri, new TextEncoder().encode(output));
}

async function findAppFolders(root: vscode.Uri): Promise<vscode.Uri[]> {
  const result: vscode.Uri[] = [];
  await walkForAppJson(root, result);
  return result;
}

async function walkForAppJson(dir: vscode.Uri, result: vscode.Uri[]): Promise<void> {
  let entries: [string, vscode.FileType][];
  try {
    entries = await vscode.workspace.fs.readDirectory(dir);
  } catch {
    return;
  }

  // Check if this directory contains app.json
  if (entries.some(([name, type]) => type === vscode.FileType.File && name.toLowerCase() === 'app.json')) {
    // Exclude .alpackages and .dependencies folders
    const dirPath = dir.path.toLowerCase();
    if (!dirPath.includes('.alpackages') && !dirPath.includes('.dependencies')) {
      result.push(dir);
    }
  }

  // Recurse into subdirectories
  for (const [name, type] of entries) {
    if (type !== vscode.FileType.Directory) { continue; }
    const lower = name.toLowerCase();
    if (lower === 'node_modules' || lower === '.git' || lower === '.alpackages' || lower === '.dependencies') { continue; }
    await walkForAppJson(vscode.Uri.joinPath(dir, name), result);
  }
}

async function upsertLaunchConfiguration(
  appFolder: vscode.Uri,
  containerName: string,
  serverUrl: string,
  alGoOverrides?: Record<string, string>,
): Promise<void> {
  const vscodeFolderUri = vscode.Uri.joinPath(appFolder, '.vscode');
  const launchUri = vscode.Uri.joinPath(vscodeFolderUri, 'launch.json');

  let launchJson: { version: string; configurations: Record<string, unknown>[] };

  try {
    const content = await vscode.workspace.fs.readFile(launchUri);
    const text = new TextDecoder().decode(content);
    launchJson = JSON.parse(stripJsonComments(text));
    if (!Array.isArray(launchJson.configurations)) {
      launchJson.configurations = [];
    }
  } catch {
    // File doesn't exist — create with empty configurations
    launchJson = { version: '0.2.0', configurations: [] };
  }

  const cfg = vscode.workspace.getConfiguration('fkh.CreateContainer');

  function resolve<T>(key: string, fallback: T): T {
    const alGoVal = alGoOverrides?.[key];
    if (alGoVal !== undefined && alGoVal !== '') {
      if (typeof fallback === 'number') { return Number(alGoVal) as T; }
      if (typeof fallback === 'boolean') { return (alGoVal === 'true') as unknown as T; }
      return alGoVal as unknown as T;
    }
    return cfg.get<T>(key, fallback);
  }

  const newConfig: Record<string, unknown> = {
    name: containerName,
    request: 'launch',
    type: 'al',
    environmentType: 'OnPrem',
    server: serverUrl.startsWith('https://') ? serverUrl : `https://${serverUrl}`,
    serverInstance: 'BC',
    port: 7049,
    authentication: 'UserPassword',
    tenant: 'default',
    startupObjectId: resolve<number>('startupObjectId', 22),
    startupObjectType: resolve<string>('startupObjectType', 'Page'),
    startupCompany: resolve<string>('startupCompany', ''),
    schemaUpdateMode: resolve<string>('schemaUpdateMode', 'Synchronize'),
    launchBrowser: resolve<boolean>('launchBrowser', true),
    usePublicURLFromServer: resolve<boolean>('usePublicURLFromServer', true),
    breakOnError: resolve<string>('breakOnError', 'All'),
    breakOnRecordWrite: resolve<boolean>('breakOnRecordWrite', false),
    enableLongRunningSqlStatements: resolve<boolean>('enableLongRunningSqlStatements', true),
    longRunningSqlStatementsThreshold: resolve<number>('longRunningSqlStatementsThreshold', 500),
    numberOfSqlStatements: resolve<number>('numberOfSqlStatements', 10),
    enableSqlInformationDebugger: resolve<boolean>('enableSqlInformationDebugger', true),
    forceUpgrade: resolve<boolean>('forceUpgrade', false),
    dependencyPublishingOption: resolve<string>('dependencyPublishingOption', 'Default'),
  };

  // Replace existing configuration with the same name, or append
  const existingIndex = launchJson.configurations.findIndex(
    (c) => typeof c === 'object' && c !== null && (c as Record<string, unknown>)['name'] === containerName,
  );

  if (existingIndex >= 0) {
    launchJson.configurations[existingIndex] = newConfig;
  } else {
    launchJson.configurations.push(newConfig);
  }

  const output = JSON.stringify(launchJson, null, 4) + '\n';

  // Ensure .vscode folder exists
  try {
    await vscode.workspace.fs.stat(vscodeFolderUri);
  } catch {
    await vscode.workspace.fs.createDirectory(vscodeFolderUri);
  }

  await vscode.workspace.fs.writeFile(launchUri, new TextEncoder().encode(output));
}

/** Strip single-line and multi-line comments from JSON (launch.json allows them). */
function stripJsonComments(text: string): string {
  let result = '';
  let i = 0;
  let inString = false;
  let escape = false;

  while (i < text.length) {
    const ch = text[i];

    if (escape) {
      result += ch;
      escape = false;
      i++;
      continue;
    }

    if (inString) {
      if (ch === '\\') { escape = true; }
      else if (ch === '"') { inString = false; }
      result += ch;
      i++;
      continue;
    }

    if (ch === '"') {
      inString = true;
      result += ch;
      i++;
      continue;
    }

    if (ch === '/' && i + 1 < text.length) {
      if (text[i + 1] === '/') {
        // Single-line comment — skip to end of line
        while (i < text.length && text[i] !== '\n') { i++; }
        continue;
      }
      if (text[i + 1] === '*') {
        // Multi-line comment
        i += 2;
        while (i + 1 < text.length && !(text[i] === '*' && text[i + 1] === '/')) { i++; }
        i += 2;
        continue;
      }
    }

    result += ch;
    i++;
  }

  return result;
}
