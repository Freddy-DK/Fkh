import * as vscode from 'vscode';
import { createReadSettingsOptions, readSettings, getRepoName, getProjects } from './readALGoSettings';
import { ProjectsTreeProvider, ProjectTreeItem, ContainersTreeProvider, ContainerTreeItem, ImagesTreeProvider, ImageTreeItem, VMsTreeProvider, VMTreeItem } from './containersTreeProvider';
import { updateLaunchJsonAfterCreate } from './updateLaunchJson';
import { PROTOCOL_VERSION, CLIENT_APP } from './protocol';

let functionCatalog: FunctionCatalogResponse | undefined;
let outputChannel: vscode.OutputChannel;
let projectsProvider: ProjectsTreeProvider;
let containersProvider: ContainersTreeProvider;
let imagesProvider: ImagesTreeProvider;
let vmsProvider: VMsTreeProvider;
let currentAccountLabel: string | undefined;
let currentBackendUrl: string | undefined;
let projectsView: vscode.TreeView<ProjectTreeItem>;
let containersView: vscode.TreeView<ContainerTreeItem>;
let imagesView: vscode.TreeView<ImageTreeItem>;
let vmsView: vscode.TreeView<VMTreeItem>;

const containerLogContents = new Map<string, string>();
const notifiedAutoStopContainers = new Set<string>();
let autoStopCheckTimer: ReturnType<typeof setInterval> | undefined;
let cachedFkhSettings: Record<string, string> | undefined;
const containerLogProvider: vscode.TextDocumentContentProvider = {
  provideTextDocumentContent(uri: vscode.Uri): string {
    return containerLogContents.get(uri.toString()) ?? '';
  }
};

function getOrgNameFromUrl(url: string): string {
  const match = url.match(/fkh-(.+?)-backend/);
  return match ? match[1] : '';
}

function updateConnectionTitle(): void {
  const org = currentBackendUrl ? getOrgNameFromUrl(currentBackendUrl) : '';
  const desc = !currentBackendUrl ? undefined
    : (containersProvider.initialized && containersProvider.connected)
      ? [currentAccountLabel, org].filter(Boolean).join(' - ') || undefined
    : containersProvider.initialized
      ? [currentAccountLabel, 'disconnected'].filter(Boolean).join(' - ')
    : undefined;
  projectsView.description = desc;
  containersView.description = desc;
  imagesView.description = desc;
  vmsView.description = desc;
}

function getBackendUrlsForAccount(): string[] {
  const raw = vscode.workspace.getConfiguration('fkh').get<string | Record<string, string | string[]>>('backendUrl', '');

  if (typeof raw === 'string') {
    const trimmed = raw.trim();
    return trimmed ? [trimmed] : [];
  }
  if (typeof raw === 'object' && raw !== null) {
    const entry = (currentAccountLabel && raw[currentAccountLabel]) ?? Object.values(raw)[0];
    if (!entry) { return []; }
    if (typeof entry === 'string') { return [entry.trim()].filter(Boolean); }
    if (Array.isArray(entry)) { return entry.map(u => u.trim()).filter(Boolean); }
  }
  return [];
}

function getBackendUrl(): string | undefined {
  if (currentBackendUrl) { return currentBackendUrl; }

  const urls = getBackendUrlsForAccount();
  if (urls.length === 1) {
    currentBackendUrl = urls[0];
    updateConnectionTitle();
    return currentBackendUrl;
  }
  if (urls.length > 1) {
    // Multiple backends — use the first as a default until the user picks one
    currentBackendUrl = urls[0];
    updateConnectionTitle();
    return currentBackendUrl;
  }

  vscode.window.showErrorMessage(
    'Fkh: Backend URL is not configured. Set "fkh.backendUrl" in your settings.',
    'Open Settings'
  ).then((action: string | undefined) => {
    if (action === 'Open Settings') {
      vscode.commands.executeCommand('workbench.action.openSettings', 'fkh.backendUrl');
    }
  });
  return undefined;
}

export function activate(context: vscode.ExtensionContext) {
  outputChannel = vscode.window.createOutputChannel('Fkh');

  projectsProvider = new ProjectsTreeProvider(getRepoName, () => containersProvider.getMyContainers());
  projectsView = vscode.window.createTreeView('fkhProjects', {
    treeDataProvider: projectsProvider,
  });

  containersProvider = new ContainersTreeProvider(getBackendUrl, getGitHubSession);
  containersView = vscode.window.createTreeView('fkhContainers', {
    treeDataProvider: containersProvider,
    showCollapseAll: true,
  });

  imagesProvider = new ImagesTreeProvider(getBackendUrl, getGitHubSession);
  imagesView = vscode.window.createTreeView('fkhImages', {
    treeDataProvider: imagesProvider,
    showCollapseAll: true,
  });

  vmsProvider = new VMsTreeProvider(getBackendUrl, getGitHubSession, () => containersProvider.getContainers());
  vmsView = vscode.window.createTreeView('fkhVMs', {
    treeDataProvider: vmsProvider,
    showCollapseAll: true,
  });

  // Delay initial population to let the extension host settle
  setTimeout(async () => {
    await loadFkhSettings();
    await containersProvider.refresh();
    updateConnectionTitle();
    projectsProvider.refresh();
    imagesProvider.refresh();
    await vmsProvider.refresh();
    vscode.commands.executeCommand('setContext', 'fkh.isAdmin', vmsProvider.visible);
    checkAutoStopNotifications();
  }, 5000);

  // Refresh containers/projects and check for approaching auto-stop times every 60 seconds
  autoStopCheckTimer = setInterval(() => void checkAutoStopNotifications(), 60_000);

  context.subscriptions.push(
    outputChannel,
    projectsView,
    containersView,
    imagesView,
    vmsView,
    vscode.workspace.onDidChangeConfiguration(async (e) => {
      if (e.affectsConfiguration('fkh.backendUrl')) {
        currentBackendUrl = undefined;
        functionCatalog = undefined;
        await containersProvider.refresh();
        updateConnectionTitle();
        projectsProvider.refresh();
        imagesProvider.refresh();
        await vmsProvider.refresh();
        vscode.commands.executeCommand('setContext', 'fkh.isAdmin', vmsProvider.visible);
      }
    }),
    vscode.workspace.registerTextDocumentContentProvider('fkh-log', containerLogProvider),
    vscode.commands.registerCommand('fkh.refreshProjects', () => projectsProvider.refresh()),
    vscode.commands.registerCommand('fkh.refreshContainers', async () => {
      await containersProvider.refresh();
      updateConnectionTitle();
      projectsProvider.refresh();
    }),
    vscode.commands.registerCommand('fkh.showAllContainers', async () => {
      containersProvider.showAll = true;
      vscode.commands.executeCommand('setContext', 'fkh.showAllContainers', true);
      await containersProvider.refresh();
      updateConnectionTitle();
      projectsProvider.refresh();
    }),
    vscode.commands.registerCommand('fkh.showMyContainers', async () => {
      containersProvider.showAll = false;
      vscode.commands.executeCommand('setContext', 'fkh.showAllContainers', false);
      await containersProvider.refresh();
      updateConnectionTitle();
      projectsProvider.refresh();
    }),
    vscode.commands.registerCommand('fkh.refreshImages', () => imagesProvider.refresh()),
    vscode.commands.registerCommand('fkh.refreshVMs', async () => {
      await vmsProvider.refresh();
      vscode.commands.executeCommand('setContext', 'fkh.isAdmin', vmsProvider.visible);
    }),
    vscode.commands.registerCommand('fkh.getContainerLog', async (item: ContainerTreeItem | ProjectTreeItem | VMTreeItem) => {
      if (!item.containerInfo) { return; }
      await showContainerLog(item.containerInfo.appLabel, item.containerInfo.name);
    }),
    vscode.commands.registerCommand('fkh.getContainerEventLog', async (item: ContainerTreeItem | ProjectTreeItem | VMTreeItem) => {
      if (!item.containerInfo) { return; }
      await downloadContainerEventLog(item.containerInfo.appLabel, item.containerInfo.name);
    }),
    vscode.commands.registerCommand('fkh.startContainer', async (item: ContainerTreeItem | ProjectTreeItem) => {
      if (!item.containerInfo) { return; }
      await invokeFunctionByName('StartContainer', { name: item.containerInfo.appLabel });
    }),
    vscode.commands.registerCommand('fkh.stopContainer', async (item: ContainerTreeItem | ProjectTreeItem) => {
      if (!item.containerInfo) { return; }
      await invokeFunctionByName('StopContainer', { name: item.containerInfo.appLabel });
    }),
    vscode.commands.registerCommand('fkh.extendAutoStop', async (item: ContainerTreeItem | ProjectTreeItem) => {
      if (!item.containerInfo) { return; }
      await invokeFunctionByName('ExtendAutoStop', { name: item.containerInfo.appLabel });
    }),
    vscode.commands.registerCommand('fkh.setAutoStop', async (item: ContainerTreeItem | ProjectTreeItem) => {
      if (!item.containerInfo) { return; }
      const value = await vscode.window.showInputBox({
        prompt: 'Auto-stop time (e.g. "2h", "18:00", "6PM")',
        placeHolder: '2h',
      });
      if (value === undefined || value.trim() === '') { return; }
      await invokeFunctionByName('SetAutoStop', { name: item.containerInfo.appLabel, autostop: value });
    }),
    vscode.commands.registerCommand('fkh.removeContainer', async (item: ContainerTreeItem | ProjectTreeItem) => {
      if (!item.containerInfo) { return; }
      const name = item.containerInfo.appLabel;
      const confirm = await vscode.window.showWarningMessage(
        `Are you sure you want to remove '${name}'? This will delete the container and its database.`,
        { modal: true },
        'Remove'
      );
      if (confirm !== 'Remove') { return; }
      await invokeFunctionByName('RemoveContainer', { name });
    }),
    vscode.commands.registerCommand('fkh.waitForContainer', async (item: ContainerTreeItem | ProjectTreeItem) => {
      if (!item.containerInfo) { return; }
      await invokeFunctionByName('WaitForContainer', { name: item.containerInfo.appLabel });
    }),
    vscode.commands.registerCommand('fkh.run', async () => {
      const catalog = await getFunctionCatalog();
      if (!catalog) { return; }

      const items = catalog.functions.map(f => ({
        label: `Fkh: ${f.name}`,
        description: f.description,
        functionName: f.name,
      }));

      const picked = await vscode.window.showQuickPick(items, {
        placeHolder: 'Select a command to run',
      });
      if (!picked) { return; }

      await invokeFunctionByName(picked.functionName);
    }),
    vscode.commands.registerCommand('fkh.createContainer', () => createContainer()),
    vscode.commands.registerCommand('fkh.createContainerForProject', async (item: ProjectTreeItem) => {
      if (!item.projectName) { return; }
      await createContainer(item.projectName);
    }),
    vscode.commands.registerCommand('fkh.createImage', () => createImage()),
    vscode.commands.registerCommand('fkh.removeImage', async (item: ImageTreeItem) => {
      if (item.tagName && item.repositoryName) {
        // Tag-level item
        const confirm = await vscode.window.showWarningMessage(
          `Are you sure you want to remove tag '${item.tagName}' from '${item.repositoryName}'? This will delete the image tag and its associated database backup.`,
          { modal: true },
          'Remove'
        );
        if (confirm !== 'Remove') { return; }
        await invokeFunctionByName('RemoveImage', { repository: item.repositoryName, tag: item.tagName });
      } else if (item.imageInfo) {
        // Repository-level item
        const confirm = await vscode.window.showWarningMessage(
          `Are you sure you want to remove repository '${item.imageInfo.repository}' and all its tags?`,
          { modal: true },
          'Remove'
        );
        if (confirm !== 'Remove') { return; }
        await invokeFunctionByName('RemoveImage', { repository: item.imageInfo.repository });
      }
    }),
    vscode.commands.registerCommand('fkh.switchAccount', async () => {
      try {
        const session = await vscode.authentication.getSession(
          'github',
          ['read:user', 'read:org'],
          { createIfNone: true, clearSessionPreference: true }
        );
        if (!session) { return; }

        currentAccountLabel = session.account.label;
        currentBackendUrl = undefined;
        functionCatalog = undefined;

        const urls = getBackendUrlsForAccount();
        if (urls.length > 1) {
          const items = urls.map(u => ({
            label: getOrgNameFromUrl(u) || u,
            description: u,
            url: u,
          }));
          const picked = await vscode.window.showQuickPick(items, {
            placeHolder: `Select a backend for ${session.account.label}`,
          });
          if (!picked) { return; }
          currentBackendUrl = picked.url;
        } else if (urls.length === 1) {
          currentBackendUrl = urls[0];
        }

        updateConnectionTitle();
        vscode.window.showInformationMessage(
          `Fkh: Signed in as ${session.account.label}` +
          (currentBackendUrl ? ` (${getOrgNameFromUrl(currentBackendUrl) || currentBackendUrl})` : '')
        );
        await containersProvider.refresh();
        updateConnectionTitle();
        projectsProvider.refresh();
        imagesProvider.refresh();
        await vmsProvider.refresh();
        vscode.commands.executeCommand('setContext', 'fkh.isAdmin', vmsProvider.visible);
      } catch {
        vscode.window.showErrorMessage('GitHub sign-in was cancelled or failed.');
      }
    })
  );
}

type FunctionParameterDefinition = {
  name: string;
  type: string;
  description: string;
  required: boolean;
  adminOnly?: boolean;
  defaultValue?: string;
};

type FunctionDefinition = {
  name: string;
  description: string;
  route: string;
  parameters: FunctionParameterDefinition[];
};

type FunctionCatalogResponse = {
  functions: FunctionDefinition[];
};

type FunctionInvokeRequest = {
  parameters: Record<string, string>;
};

async function getPublicIp(): Promise<string | undefined> {
  try {
    const response = await fetch('https://api.ipify.org?format=text');
    if (response.ok) {
      return (await response.text()).trim();
    }
  } catch { /* ignore */ }
  return undefined;
}

async function getGitHubSession(): Promise<vscode.AuthenticationSession | undefined> {
  try {
    // Try to get an existing session silently first (works in vscode.dev where
    // the user is already signed in via GitHub).
    // Always require read:org — without it the backend's team membership check fails.
    const existing = await vscode.authentication.getSession(
      'github',
      ['read:user', 'read:org'],
      { createIfNone: false, silent: true }
    );
    if (existing) {
      if (currentAccountLabel !== existing.account.label) {
        currentAccountLabel = existing.account.label;
        currentBackendUrl = undefined;
      }
      return existing;
    }

    // No silent session — prompt interactively and show account picker if
    // multiple GitHub accounts are signed in.
    const session = await vscode.authentication.getSession(
      'github',
      ['read:user', 'read:org'],
      { createIfNone: true, clearSessionPreference: true }
    );
    if (session) {
      if (currentAccountLabel !== session.account.label) {
        currentAccountLabel = session.account.label;
        currentBackendUrl = undefined;
      }
    }
    return session;
  } catch {
    vscode.window.showErrorMessage('GitHub sign-in was cancelled or failed.');
    return undefined;
  }
}

async function getFunctionCatalog(): Promise<FunctionCatalogResponse | undefined> {
  if (functionCatalog) {
    return functionCatalog;
  }

  const baseUrl = getBackendUrl();
  if (!baseUrl) { return undefined; }

  try {
    const response = await fetch(`${baseUrl}/functions`, { method: 'GET' });
    if (!response.ok) {
      const error = await response.text();
      const action = await vscode.window.showErrorMessage(
        `Failed to fetch function metadata: ${error}. Check that fkh.backendUrl is correct.`,
        'Open Settings'
      );
      if (action === 'Open Settings') {
        await vscode.commands.executeCommand('workbench.action.openSettings', 'fkh.backendUrl');
      }
      return undefined;
    }

    functionCatalog = await response.json() as FunctionCatalogResponse;
    return functionCatalog;
  } catch (err) {
    const action = await vscode.window.showErrorMessage(
      `Could not reach the Fkh backend at ${baseUrl}. Check that fkh.backendUrl is correct (${err instanceof Error ? err.message : String(err)}).`,
      'Open Settings'
    );
    if (action === 'Open Settings') {
      await vscode.commands.executeCommand('workbench.action.openSettings', 'fkh.backendUrl');
    }
    return undefined;
  }
}

async function loadFkhSettings(): Promise<void> {
  try {
    const session = await getGitHubSession();
    if (!session) { return; }
    const projects = await getProjects();
    const project = projects[0] ?? '';
    const options = await createReadSettingsOptions(session.accessToken, project);
    if (!options?.baseFolder) { return; }
    const settings = await readSettings(options);
    const fkh = settings['fkh'];
    cachedFkhSettings = (fkh && typeof fkh === 'object') ? fkh as Record<string, string> : undefined;
  } catch {
    // Silently ignore — settings may not be available (no repo, no AL-Go)
  }
}

async function promptForParameters(
  definition: FunctionDefinition,
  prefilled: Record<string, string> = {},
  context?: string,
  prefilledDefaults: Record<string, string> = {}
): Promise<Record<string, string> | undefined> {
  const config = vscode.workspace.getConfiguration('fkh');
  const isAdmin = vmsProvider?.visible ?? false;

  // Apply cached AL-Go fkh settings for this function
  if (cachedFkhSettings) {
    const prefix = `${definition.name}.`;
    for (const [key, value] of Object.entries(cachedFkhSettings)) {
      if (key.startsWith(prefix)) {
        const paramKey = key.substring(prefix.length);
        const strValue = String(value ?? '').trim();
        if (paramKey.endsWith('?')) {
          // Trailing ? means show the parameter with value as default (don't override explicit prefilledDefaults)
          const cleanKey = paramKey.slice(0, -1);
          if (!(cleanKey in prefilledDefaults) && strValue) {
            prefilledDefaults[cleanKey] = strValue;
          }
        } else {
          // Hard override — skip prompting for this param (don't override explicit prefilled)
          if (!(paramKey in prefilled)) {
            prefilled[paramKey] = strValue;
          }
        }
      }
    }
  }

  // Resolve defaults: prefilled > settings > auto-detect > catalog default
  const resolvedDefaults: Record<string, string> = {};
  const promptParams: FunctionParameterDefinition[] = [];

  // Prompt for file-type parameters first using a file picker
  for (const param of definition.parameters) {
    if (param.type.toLowerCase() !== 'file') { continue; }
    const prefilledKey = Object.keys(prefilled).find(
      k => k.toLowerCase() === param.name.toLowerCase()
    );
    if (prefilledKey) {
      if (prefilled[prefilledKey]) {
        resolvedDefaults[param.name] = prefilled[prefilledKey];
      }
      continue;
    }
    const defaultKey = Object.keys(prefilledDefaults).find(
      k => k.toLowerCase() === param.name.toLowerCase()
    );
    if (defaultKey && prefilledDefaults[defaultKey]) {
      resolvedDefaults[param.name] = prefilledDefaults[defaultKey];
    }

    const uris = await vscode.window.showOpenDialog({
      canSelectMany: false,
      openLabel: `Select ${param.name}`,
      title: param.description,
      filters: param.name.toLowerCase().includes('app') ? { 'App files': ['app'], 'All files': ['*'] } : undefined,
    });
    if (!uris || uris.length === 0) {
      if (param.required) { return undefined; }
      continue;
    }
    resolvedDefaults[param.name] = uris[0].fsPath;
  }

  for (const param of definition.parameters) {
    // Skip file-type params (already handled above)
    if (param.type.toLowerCase() === 'file') { continue; }
    // Hide admin-only params from non-admins
    if (param.adminOnly && !isAdmin) {
      continue;
    }
    const prefilledKey = Object.keys(prefilled).find(
      k => k.toLowerCase() === param.name.toLowerCase()
    );
    if (prefilledKey) {
      if (prefilled[prefilledKey]) {
        resolvedDefaults[param.name] = prefilled[prefilledKey];
      }
      continue;
    }

    // Keys with ? suffix: use value as default but still show for prompting
    const defaultKey = Object.keys(prefilledDefaults).find(
      k => k.toLowerCase() === param.name.toLowerCase()
    );
    if (defaultKey && prefilledDefaults[defaultKey]) {
      resolvedDefaults[param.name] = prefilledDefaults[defaultKey];
    }

    const settingKey = `${definition.name}.${param.name}`;
    const inspected = config.inspect<string>(settingKey);
    const settingValue = (inspected?.workspaceFolderValue ?? inspected?.workspaceValue ?? inspected?.globalValue);
    if (settingValue !== undefined) {
      const trimmed = settingValue.trim();
      if (trimmed) {
        resolvedDefaults[param.name] = trimmed;
      }
      // Setting explicitly exists — skip prompting (even if empty)
      continue;
    }

    // Auto-detect public IP for parameters named 'ip'
    if (param.name.toLowerCase() === 'ip') {
      const detectedIp = await getPublicIp();
      if (detectedIp) {
        resolvedDefaults[param.name] = detectedIp;
      }
    }

    promptParams.push(param);
  }

  // If nothing to prompt for, return immediately
  if (promptParams.length === 0) {
    return resolvedDefaults;
  }

  // Single parameter: use simple input box
  if (promptParams.length === 1) {
    const param = promptParams[0];
    const defaultVal = resolvedDefaults[param.name] ?? param.defaultValue ?? '';
    const settingKey = `${definition.name}.${param.name}`;
    const isPassword = param.name.toLowerCase().includes('password');

    while (true) {
      const value = await vscode.window.showInputBox({
        prompt: `${param.name}: ${param.description} (Tip: set "fkh.${settingKey}" in settings to skip this prompt)`,
        placeHolder: defaultVal,
        value: defaultVal,
        password: isPassword,
        ignoreFocusOut: true,
      });

      if (value === undefined) {
        return undefined;
      }

      if (value.trim().length > 0) {
        resolvedDefaults[param.name] = value.trim();
        return resolvedDefaults;
      }

      if (!param.required) {
        return resolvedDefaults;
      }

      vscode.window.showWarningMessage(`${param.name} is required.`);
    }
  }

  // Multiple parameters: use webview form

  return new Promise<Record<string, string> | undefined>((resolve) => {
    const panel = vscode.window.createWebviewPanel(
      'fkhParameters',
      context ? `${definition.name} – ${context}` : `${definition.name}`,
      vscode.ViewColumn.Active,
      { enableScripts: true }
    );

    let resolved = false;

    panel.webview.onDidReceiveMessage((msg: { command: string; parameters?: Record<string, string> }) => {
      if (msg.command === 'submit' && msg.parameters) {
        resolved = true;
        const result = { ...resolvedDefaults };
        for (const [key, value] of Object.entries(msg.parameters)) {
          if (value.trim().length > 0) {
            result[key] = value.trim();
          }
        }
        panel.dispose();
        resolve(result);
      } else if (msg.command === 'cancel') {
        resolved = true;
        panel.dispose();
        resolve(undefined);
      }
    });

    panel.onDidDispose(() => {
      if (!resolved) { resolve(undefined); }
    });

    const fieldsHtml = promptParams.map(param => {
      const defaultVal = resolvedDefaults[param.name] ?? param.defaultValue ?? '';
      const escapedDefault = defaultVal.replace(/&/g, '&amp;').replace(/"/g, '&quot;').replace(/</g, '&lt;');
      let desc = param.description;
      const escapedDesc = desc.replace(/&/g, '&amp;').replace(/</g, '&lt;');
      const requiredBadge = param.required ? '<span class="required">required</span>' : '';
      const isPassword = param.name.toLowerCase().includes('password');

      if (param.type.toLowerCase() === 'boolean') {
        const checked = defaultVal.toLowerCase() === 'true' ? 'checked' : '';
        return `<div class="field">
          <label><input type="checkbox" name="${param.name}" ${checked} /> ${param.name} ${requiredBadge}</label>
          <div class="desc">${escapedDesc}</div>
        </div>`;
      }

      return `<div class="field">
        <label for="${param.name}">${param.name} ${requiredBadge}</label>
        <div class="desc">${escapedDesc}</div>
        <input type="${isPassword ? 'password' : 'text'}" id="${param.name}" name="${param.name}"
          value="${escapedDefault}" placeholder="${escapedDefault || (param.required ? '(required)' : '(optional)')}"${param.required ? ' required' : ''} />
      </div>`;
    }).join('\n');

    panel.webview.html = `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline';">
<style>
  body { font-family: var(--vscode-font-family, sans-serif); padding: 16px; color: var(--vscode-foreground); background: var(--vscode-editor-background); }
  h2 { margin: 0 0 4px 0; }
  .subtitle { color: var(--vscode-descriptionForeground); margin-bottom: 16px; }
  .field { margin-bottom: 14px; }
  label { font-weight: 600; display: block; margin-bottom: 2px; }
  .desc { color: var(--vscode-descriptionForeground); font-size: 12px; margin-bottom: 4px; }
  .required { color: var(--vscode-errorForeground); font-size: 11px; font-weight: normal; }
  input[type="text"], input[type="password"] {
    width: 100%; box-sizing: border-box; padding: 6px 8px;
    background: var(--vscode-input-background); color: var(--vscode-input-foreground);
    border: 1px solid var(--vscode-input-border, transparent); border-radius: 2px;
  }
  input[type="text"]:focus, input[type="password"]:focus { outline: 1px solid var(--vscode-focusBorder); }
  input:invalid { border-color: var(--vscode-errorForeground); }
  .buttons { margin-top: 20px; display: flex; gap: 8px; }
  button {
    padding: 6px 16px; border: none; border-radius: 2px; cursor: pointer; font-size: 13px;
  }
  .btn-primary { background: var(--vscode-button-background); color: var(--vscode-button-foreground); }
  .btn-primary:hover { background: var(--vscode-button-hoverBackground); }
  .btn-secondary { background: var(--vscode-button-secondaryBackground); color: var(--vscode-button-secondaryForeground); }
  .btn-secondary:hover { background: var(--vscode-button-secondaryHoverBackground); }
</style>
</head>
<body>
  <h2>${definition.name}${context ? ` for ${context}` : ''}</h2>
  <div class="subtitle">${definition.description}</div>
  <form id="paramForm">
    ${fieldsHtml}
    <div class="buttons">
      <button type="submit" class="btn-primary">Run</button>
      <button type="button" class="btn-secondary" id="cancelBtn">Cancel</button>
    </div>
  </form>
  <script>
    const vscode = acquireVsCodeApi();
    const form = document.getElementById('paramForm');
    form.addEventListener('submit', (e) => {
      e.preventDefault();
      const params = {};
      const inputs = form.querySelectorAll('input');
      for (const el of inputs) {
        if (!el.getAttribute('name')) continue;
        const paramName = el.getAttribute('name');
        if (el.type === 'checkbox') {
          params[paramName] = el.checked ? 'true' : 'false';
        } else {
          params[paramName] = el.value;
        }
      }
      vscode.postMessage({ command: 'submit', parameters: params });
    });
    document.getElementById('cancelBtn').addEventListener('click', () => {
      vscode.postMessage({ command: 'cancel' });
    });
    // Focus first input
    const first = form.querySelector('input');
    if (first) first.focus();
  </script>
</body>
</html>`;
  });
}

function logOutput(message: string, isError = false): void {
  outputChannel.appendLine(message);
  outputChannel.show(true);
  if (isError) { vscode.window.showErrorMessage(message); }
}

function formatJsonResult(obj: unknown, indent = 0): string {
  if (obj === null || obj === undefined) { return ''; }
  if (typeof obj === 'string' || typeof obj === 'number' || typeof obj === 'boolean') {
    return String(obj);
  }
  if (Array.isArray(obj)) {
    return obj.map(item => formatJsonResult(item, indent)).join('\n');
  }
  if (typeof obj === 'object') {
    const prefix = '  '.repeat(indent);
    return Object.entries(obj as Record<string, unknown>)
      .filter(([, v]) => v !== null && v !== undefined)
      .map(([k, v]) => {
        const label = k.charAt(0).toUpperCase() + k.slice(1).replace(/([A-Z])/g, ' $1');
        if (typeof v === 'object') {
          return `${prefix}${label}:\n${formatJsonResult(v, indent + 1)}`;
        }
        return `${prefix}${label}: ${v}`;
      })
      .join('\n');
  }
  return String(obj);
}

async function invokeFunctionByName(functionName: string, prefilled: Record<string, string> = {}, context?: string, prefilledDefaults: Record<string, string> = {}): Promise<Record<string, unknown> | undefined> {
  const catalog = await getFunctionCatalog();
  if (!catalog) { return; }

  const definition = catalog.functions.find(f => f.name.toLowerCase() === functionName.toLowerCase());
  if (!definition) {
    vscode.window.showErrorMessage(`Function '${functionName}' is not available.`);
    return;
  }

  const parameters = await promptForParameters(definition, prefilled, context, prefilledDefaults);
  if (!parameters) { return; }

  // Send the client's timezone so the server can resolve time-of-day autostop values
  parameters['_timezone'] = vscode.workspace.getConfiguration('fkh').get<string>('timezone', '').trim()
    || Intl.DateTimeFormat().resolvedOptions().timeZone;

  const session = await getGitHubSession();
  if (!session) { return; }

  // Detect file-type parameters
  const fileParamNames = new Set(
    definition.parameters
      .filter(p => p.type.toLowerCase() === 'file')
      .map(p => p.name.toLowerCase())
  );

  const filesToUpload: Record<string, string> = {};
  for (const [key, value] of Object.entries(parameters)) {
    if (fileParamNames.has(key.toLowerCase()) && value) {
      filesToUpload[key] = value;
      delete parameters[key];
    }
  }

  const hasFiles = Object.keys(filesToUpload).length > 0;

  let invokeResult: Record<string, unknown> | undefined;

  await vscode.window.withProgress(
    {
      location: vscode.ProgressLocation.Notification,
      title: `${definition.name}: ${definition.description}`,
      cancellable: true,
    },
    async (progress, cancelToken) => {
      try {
        const url = `${getBackendUrl()}/${definition.route}`;

        while (true) {
          if (cancelToken.isCancellationRequested) {
            logOutput(`[${definition.name}] Cancelled by user.`);
            return;
          }

          let response: Response;

          if (hasFiles) {
            // Build multipart/form-data manually using Blob API
            const formData = new FormData();
            formData.append('parameters', JSON.stringify({ parameters }));
            for (const [paramName, filePath] of Object.entries(filesToUpload)) {
              const fileUri = vscode.Uri.file(filePath);
              const fileBuffer = await vscode.workspace.fs.readFile(fileUri);
              const blob = new Blob([new Uint8Array(fileBuffer) as BlobPart], { type: 'application/octet-stream' });
              const fileName = filePath.replace(/\\/g, '/').split('/').pop() ?? 'file';
              formData.append(paramName, blob, fileName);
            }

            response = await fetch(url, {
              method: 'POST',
              headers: {
                Authorization: `Bearer ${session.accessToken}`,
                'X-Fkh-Protocol-Version': String(PROTOCOL_VERSION),
                'X-Fkh-Client': CLIENT_APP,
              },
              body: formData,
            });
          } else {
            const body: FunctionInvokeRequest = { parameters };
            response = await fetch(url, {
              method: 'POST',
              headers: {
                'Content-Type': 'application/json',
                Authorization: `Bearer ${session.accessToken}`,
                'X-Fkh-Protocol-Version': String(PROTOCOL_VERSION),
                'X-Fkh-Client': CLIENT_APP,
              },
              body: JSON.stringify(body),
            });
          }

          if (response.status === 202) {
            const result = await response.json() as { message: string; retryAfterSeconds?: number };
            const retrySeconds = result.retryAfterSeconds ?? parseInt(response.headers.get('Retry-After') ?? '0', 10);
            if (retrySeconds > 0) {
              progress.report({ message: result.message });
              logOutput(`[${definition.name}] ${result.message} Retrying in ${retrySeconds}s...`);
              await new Promise<void>((resolve) => {
                const timer = setTimeout(resolve, retrySeconds * 1000);
                cancelToken.onCancellationRequested(() => { clearTimeout(timer); resolve(); });
              });
              continue;
            }
          }

          if (response.ok) {
            const result = await response.json() as Record<string, unknown>;
            logOutput(`[${definition.name}] ${formatJsonResult(result)}`);
            invokeResult = result;
          } else {
            const responseText = await response.text();
            const error = response.status === 401 || response.status === 403
              ? `Access denied (${response.status}). ${responseText || 'Make sure your GitHub account is a member of an authorized team.'}`
              : `Failed (${response.status}): ${responseText}`;
            logOutput(`[${definition.name}] ${error}`, true);
          }
          break;
        }
      } catch (err) {
        logOutput(`[${definition.name}] Could not reach the provisioning service: ${err instanceof Error ? err.message : String(err)}`, true);
      }

      await containersProvider.refresh();
      updateConnectionTitle();
      projectsProvider.refresh();
      imagesProvider.refresh();
    }
  );

  return invokeResult;
}

async function showContainerLog(appLabel: string, containerName: string): Promise<void> {
  const baseUrl = getBackendUrl();
  if (!baseUrl) { return; }

  const session = await getGitHubSession();
  if (!session) { return; }

  await vscode.window.withProgress(
    {
      location: vscode.ProgressLocation.Notification,
      title: `Fetching logs for ${containerName}...`,
      cancellable: false,
    },
    async () => {
      try {
        const body: FunctionInvokeRequest = { parameters: { name: containerName } };
        const response = await fetch(`${baseUrl}/GetContainerLog`, {
          method: 'POST',
          headers: {
            'Content-Type': 'application/json',
            Authorization: `Bearer ${session.accessToken}`,
            'X-Fkh-Protocol-Version': String(PROTOCOL_VERSION),
            'X-Fkh-Client': CLIENT_APP,
          },
          body: JSON.stringify(body),
        });

        if (response.ok) {
          const result = await response.json() as { logs: string };
          const timestamp = new Date().toISOString().replace(/[:.]/g, '-');
          const uri = vscode.Uri.parse(`fkh-log:${containerName}-${timestamp}.log`);
          containerLogContents.set(uri.toString(), result.logs ?? '');
          const doc = await vscode.workspace.openTextDocument(uri);
          await vscode.window.showTextDocument(doc, { preview: true });
        } else {
          const error = response.status === 401 || response.status === 403
            ? `Access denied (${response.status}).`
            : `Failed (${response.status}): ${await response.text()}`;
          logOutput(`[GetContainerLog] ${error}`, true);
        }
      } catch (err) {
        logOutput(`[GetContainerLog] Could not reach the provisioning service: ${err instanceof Error ? err.message : String(err)}`, true);
      }
    }
  );
}

async function downloadContainerEventLog(appLabel: string, containerName: string): Promise<void> {
  const baseUrl = getBackendUrl();
  if (!baseUrl) { return; }

  const session = await getGitHubSession();
  if (!session) { return; }

  // Prompt for save location before downloading
  const defaultFileName = `${containerName}-eventlog.evtx`;
  const saveUri = await vscode.window.showSaveDialog({
    defaultUri: vscode.Uri.file(defaultFileName),
    filters: { 'Event Log': ['evtx'], 'All files': ['*'] },
    title: 'Save Event Log As',
  });
  if (!saveUri) { return; }

  await vscode.window.withProgress(
    {
      location: vscode.ProgressLocation.Notification,
      title: `Downloading event log from ${containerName}...`,
      cancellable: false,
    },
    async () => {
      try {
        const body: FunctionInvokeRequest = { parameters: { name: containerName } };
        const response = await fetch(`${baseUrl}/GetContainerEventLog`, {
          method: 'POST',
          headers: {
            'Content-Type': 'application/json',
            Authorization: `Bearer ${session.accessToken}`,
            'X-Fkh-Protocol-Version': String(PROTOCOL_VERSION),
            'X-Fkh-Client': CLIENT_APP,
          },
          body: JSON.stringify(body),
        });

        if (response.ok) {
          const result = await response.json() as { eventLog: string; container: string; fileName: string };
          if (!result.eventLog) {
            logOutput(`[GetContainerEventLog] No event log data returned.`, true);
            return;
          }

          // Decode base64 to binary — works in Node, web, and Codespaces
          const binaryString = atob(result.eventLog);
          const bytes = new Uint8Array(binaryString.length);
          for (let i = 0; i < binaryString.length; i++) {
            bytes[i] = binaryString.charCodeAt(i);
          }

          await vscode.workspace.fs.writeFile(saveUri, bytes);
          logOutput(`[GetContainerEventLog] Event log saved to ${saveUri.fsPath || saveUri.toString()}`);
          const openAction = await vscode.window.showInformationMessage(
            `Event log saved to ${saveUri.fsPath || saveUri.toString()}`,
            'Open File', 'Reveal in Explorer'
          );
          if (openAction === 'Open File') {
            await vscode.env.openExternal(saveUri);
          } else if (openAction === 'Reveal in Explorer') {
            await vscode.commands.executeCommand('revealFileInOS', saveUri);
          }
        } else {
          const error = response.status === 401 || response.status === 403
            ? `Access denied (${response.status}).`
            : `Failed (${response.status}): ${await response.text()}`;
          logOutput(`[GetContainerEventLog] ${error}`, true);
        }
      } catch (err) {
        logOutput(`[GetContainerEventLog] Could not reach the provisioning service: ${err instanceof Error ? err.message : String(err)}`, true);
      }
    }
  );
}

async function createContainer(project?: string): Promise<void> {
  const session = await getGitHubSession();
  if (!session) { return; }

  const options = await createReadSettingsOptions(session.accessToken, project);
  if (!options) { return; }
  if (!options.baseFolder) {
    vscode.window.showErrorMessage('Fkh: No Git repository found in the current workspace.');
    return;
  }

  let settings: Record<string, unknown>;
  try {
    settings = await readSettings(options);
  } catch (err) {
    vscode.window.showErrorMessage(`Fkh: Failed to read AL-Go settings: ${err instanceof Error ? err.message : String(err)}`);
    return;
  }

  const artifact = String(settings['artifact'] ?? '');
  const country = String(settings['country'] ?? 'us');

  // Update the cached fkh settings from the freshly-read AL-Go settings
  const fkhSettings = settings['fkh'];
  cachedFkhSettings = (fkhSettings && typeof fkhSettings === 'object') ? fkhSettings as Record<string, string> : undefined;

  outputChannel.appendLine('--- ReadSettings Options ---');
  outputChannel.appendLine(`  baseFolder: ${options.baseFolder.toString()}`);
  outputChannel.appendLine(`  repoName: ${options.repoName}`);
  outputChannel.appendLine(`  project: ${options.project || '(empty)'}`);
  outputChannel.appendLine(`  buildMode: ${options.buildMode}`);
  outputChannel.appendLine(`  workflowName: ${options.workflowName || '(empty)'}`);
  outputChannel.appendLine(`  userName: ${options.userName}`);
  outputChannel.appendLine(`  branchName: ${options.branchName}`);
  outputChannel.appendLine(`  orgSettingsVariableValue: ${options.orgSettingsVariableValue || '(empty)'}`);
  outputChannel.appendLine(`  repoSettingsVariableValue: ${options.repoSettingsVariableValue || '(empty)'}`);
  outputChannel.appendLine(`  environmentSettingsVariableValue: ${options.environmentSettingsVariableValue || '(empty)'}`);
  outputChannel.appendLine(`  environmentName: ${options.environmentName || '(empty)'}`);
  outputChannel.appendLine(`  customSettings: ${options.customSettings || '(empty)'}`);

  const artifactUrl = artifact || `///${country}/latest`;

  outputChannel.appendLine('--- Resolved Settings ---');
  outputChannel.appendLine(`  Country: ${country}`);
  outputChannel.appendLine(`  Artifact: ${artifactUrl}${!artifact ? ' (defaulted)' : ''}`);
  outputChannel.show(true);

  const projectContext = options.project ? `${options.repoName}/${options.project}` : options.repoName;

  const result = await invokeFunctionByName('CreateContainer', { artifactUrl, repo: options.repoName, project: options.project || '' }, projectContext);

  // Update launch.json if configured and container was created successfully
  if (result) {
    const serverUrl = String(result.fqdn || '');
    const containerName = String(result.deployment || '');
    if (serverUrl && containerName) {
      try {
        // Extract CreateContainer hard overrides from cached settings for launch.json
        const launchOverrides: Record<string, string> = {};
        if (cachedFkhSettings) {
          const prefix = 'CreateContainer.';
          for (const [key, value] of Object.entries(cachedFkhSettings)) {
            if (key.startsWith(prefix) && !key.endsWith('?')) {
              launchOverrides[key.substring(prefix.length)] = String(value ?? '');
            }
          }
        }
        await updateLaunchJsonAfterCreate(containerName, serverUrl, options.project || '', launchOverrides);
      } catch (err) {
        logOutput(`[UpdateLaunchJson] ${err instanceof Error ? err.message : String(err)}`, true);
      }
    }
  }
}

async function createImage(): Promise<void> {
  const session = await getGitHubSession();
  if (!session) { return; }

  const options = await createReadSettingsOptions(session.accessToken);
  if (!options) { return; }
  if (!options.baseFolder) {
    vscode.window.showErrorMessage('Fkh: No Git repository found in the current workspace.');
    return;
  }

  let settings: Record<string, unknown>;
  try {
    settings = await readSettings(options);
  } catch (err) {
    vscode.window.showErrorMessage(`Fkh: Failed to read AL-Go settings: ${err instanceof Error ? err.message : String(err)}`);
    return;
  }

  const artifact = String(settings['artifact'] ?? '');
  if (!artifact) {
    vscode.window.showWarningMessage('Fkh: No artifact setting found in AL-Go settings.');
    return;
  }

  outputChannel.appendLine(`[CreateImage] Artifact: ${artifact}`);
  outputChannel.show(true);

  await invokeFunctionByName('CreateImage', { artifactUrl: artifact });
}

export function deactivate() {
  if (autoStopCheckTimer) {
    clearInterval(autoStopCheckTimer);
    autoStopCheckTimer = undefined;
  }
}

async function checkAutoStopNotifications(): Promise<void> {
  await containersProvider.refresh();
  projectsProvider.refresh();

  const containers = containersProvider.getMyContainers();
  const now = Date.now();

  for (const c of containers) {
    if (!c.autoStop) { continue; }

    // Parse "yyyy-MM-dd HH:mm (in Xh XXm)" — extract the absolute time part
    const match = c.autoStop.match(/^(\d{4}-\d{2}-\d{2} \d{2}:\d{2})/);
    if (!match) { continue; }

    const stopTime = new Date(match[1].replace(' ', 'T')).getTime();
    if (isNaN(stopTime)) { continue; }

    const minutesLeft = (stopTime - now) / 60_000;
    const key = `${c.name}|${match[1]}`;

    if (minutesLeft > 0 && minutesLeft <= 15 && !notifiedAutoStopContainers.has(key)) {
      notifiedAutoStopContainers.add(key);
      const label = c.name;
      vscode.window.showWarningMessage(
        `Container "${label}" will shut down in ${Math.ceil(minutesLeft)} minutes.`,
        'Extend 2h',
        'Dismiss'
      ).then(async (action) => {
        if (action === 'Extend 2h') {
          notifiedAutoStopContainers.delete(key);
          await invokeFunctionByName('ExtendAutoStop', { name: label });
        }
      });
    }
  }
}