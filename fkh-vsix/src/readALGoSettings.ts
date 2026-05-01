import * as vscode from 'vscode';

const ALGoFolderName = '.AL-Go';
const ALGoSettingsFileName = 'settings.json';
const RepoSettingsFileName = 'AL-Go-Settings.json';
const CustomTemplateRepoSettingsFileName = 'AL-Go-TemplateRepoSettings.doNotEdit.json';
const CustomTemplateProjectSettingsFileName = 'AL-Go-TemplateProjectSettings.doNotEdit.json';

// Relative path segments (joined via Uri at point of use)
const ALGoSettingsPath = [ALGoFolderName, ALGoSettingsFileName];
const RepoSettingsPath = ['.github', RepoSettingsFileName];
const CustomTemplateRepoSettingsPath = ['.github', CustomTemplateRepoSettingsFileName];
const CustomTemplateProjectSettingsPath = ['.github', CustomTemplateProjectSettingsFileName];

export interface ReadSettingsOptions {
  baseFolder: vscode.Uri;
  repoName: string;
  project: string;
  buildMode: string;
  workflowName: string;
  userName: string;
  branchName: string;
  orgSettingsVariableValue: string;
  repoSettingsVariableValue: string;
  environmentSettingsVariableValue: string;
  environmentName: string;
  customSettings: string;
}

interface GitExtensionAPI {
  repositories: GitRepository[];
}

interface GitRepository {
  rootUri: vscode.Uri;
  state: {
    HEAD?: { name?: string };
    remotes: { name: string; fetchUrl?: string; pushUrl?: string }[];
  };
}

function getGitAPI(): GitExtensionAPI | undefined {
  const gitExtension = vscode.extensions.getExtension<{ getAPI(version: number): GitExtensionAPI }>('vscode.git');
  if (!gitExtension?.isActive) { return undefined; }
  return gitExtension.exports.getAPI(1);
}

function getGitRepository(): GitRepository | undefined {
  const git = getGitAPI();
  if (!git || git.repositories.length === 0) { return undefined; }
  if (git.repositories.length === 1) { return git.repositories[0]; }

  // When multiple repositories are registered (e.g. root repo + submodules),
  // pick the outermost (root) repository. We look for repositories that are
  // ancestors of (or equal to) the first workspace folder, then pick the one
  // with the shortest path to avoid accidentally using a submodule.
  // Note: vscode.Uri.path always uses forward slashes on all platforms,
  // so string-based path comparisons are safe here.
  const workspaceFolderPath = vscode.workspace.workspaceFolders?.[0]?.uri.path ?? '';
  const isAncestorOrEqual = (repoPath: string, targetPath: string): boolean =>
    targetPath === repoPath || targetPath.startsWith(repoPath + '/');

  const candidates = workspaceFolderPath
    ? git.repositories.filter(r => isAncestorOrEqual(r.rootUri.path, workspaceFolderPath))
    : [];
  // Fall back to all repositories if none matched the workspace folder.
  // git.repositories is non-empty at this point (checked above), so reduce is safe.
  const pool = candidates.length > 0 ? candidates : git.repositories;
  return pool.reduce((best, r) => r.rootUri.path.length < best.rootUri.path.length ? r : best);
}

export function getGitRootUri(): vscode.Uri | undefined {
  const repo = getGitRepository();
  if (repo) { return repo.rootUri; }
  // Fallback for web environments where vscode.git may not be available
  const folders = vscode.workspace.workspaceFolders;
  return folders?.[0]?.uri;
}

export function getRepoName(): string {
  const repo = getGitRepository();
  if (!repo) { return ''; }
  const remote = repo.state.remotes.find(r => r.name === 'origin');
  const url = remote?.fetchUrl ?? remote?.pushUrl ?? '';
  // Extract owner/repo from https or ssh URL
  const match = url.match(/[/:]([^/]+\/[^/.]+?)(?:\.git)?$/);
  return match?.[1] ?? '';
}

function getBranchName(): string {
  const repo = getGitRepository();
  return repo?.state.HEAD?.name ?? '';
}

function getGitUserName(): string {
  const repo = getGitRepository();
  if (!repo) { return ''; }
  const remote = repo.state.remotes.find(r => r.name === 'origin');
  const url = remote?.fetchUrl ?? remote?.pushUrl ?? '';
  const match = url.match(/[/:]([^/]+)\/[^/.]+?(?:\.git)?$/);
  return match?.[1] ?? '';
}

export async function getProjects(): Promise<string[]> {
  const gitRoot = getGitRootUri();
  if (!gitRoot) { return []; }

  const repoName = getRepoName();

  const entries = await vscode.workspace.fs.readDirectory(gitRoot);
  const candidates: string[] = [];
  for (const [name, type] of entries) {
    if (type !== vscode.FileType.Directory) { continue; }
    if (await uriExists(vscode.Uri.joinPath(gitRoot, name, ...ALGoSettingsPath))) {
      candidates.push(name);
    }
  }

  // If no subfolders have .AL-Go, check if root has one
  if (candidates.length === 0 && await uriExists(vscode.Uri.joinPath(gitRoot, ...ALGoSettingsPath))) {
    candidates.push('.');
  }

  const projects: string[] = [];
  for (const project of candidates) {
    const settings = await readSettings({
      baseFolder: gitRoot,
      repoName,
      project,
      buildMode: 'Default',
      workflowName: '',
      userName: '',
      branchName: '',
      orgSettingsVariableValue: '',
      repoSettingsVariableValue: '',
      environmentSettingsVariableValue: '',
      environmentName: '',
      customSettings: '',
    });
    const fkh = settings.fkh as Record<string, unknown> | undefined;
    if (fkh?.ignoreProject === true) { continue; }
    projects.push(project);
  }

  return projects;
}

export async function getProject(): Promise<string | undefined> {
  const projects = await getProjects();

  if (projects.length === 0) {
    return undefined;
  }

  if (projects.length === 1) {
    return projects[0];
  }

  const picked = await vscode.window.showQuickPick(
    projects.map(p => ({ label: p })),
    { placeHolder: 'Select an AL-Go project' }
  );
  return picked?.label;
}

async function getGitHubVariable(token: string, owner: string, repo: string, variableName: string): Promise<string> {
  // Try repo-level variable first
  try {
    const repoResp = await fetch(
      `https://api.github.com/repos/${owner}/${repo}/actions/variables/${variableName}`,
      { headers: { Authorization: `Bearer ${token}`, Accept: 'application/vnd.github+json' } }
    );
    if (repoResp.ok) {
      const data = await repoResp.json() as { value: string };
      return data.value;
    }
  } catch { /* fall through to org */ }

  // Try org-level variable
  try {
    const orgResp = await fetch(
      `https://api.github.com/orgs/${owner}/actions/variables/${variableName}`,
      { headers: { Authorization: `Bearer ${token}`, Accept: 'application/vnd.github+json' } }
    );
    if (orgResp.ok) {
      const data = await orgResp.json() as { value: string };
      return data.value;
    }
  } catch { /* not available */ }

  return '';
}

export async function createReadSettingsOptions(githubToken: string, preselectedProject?: string): Promise<ReadSettingsOptions | undefined> {
  const repoFullName = getRepoName();
  const [owner, repo] = repoFullName.includes('/') ? repoFullName.split('/') : ['', repoFullName];

  const project = preselectedProject ?? await getProject();
  if (project === undefined) { return undefined; }

  const [orgSettings, repoSettings] = await Promise.all([
    getGitHubVariable(githubToken, owner, repo, 'ALGoOrgSettings'),
    getGitHubVariable(githubToken, owner, repo, 'ALGoRepoSettings'),
  ]);

  const baseFolder = getGitRootUri();
  if (!baseFolder) { return undefined; }

  return {
    baseFolder,
    repoName: repoFullName,
    project: project,
    buildMode: 'Default',
    workflowName: '',
    userName: getGitUserName(),
    branchName: getBranchName(),
    orgSettingsVariableValue: orgSettings,
    repoSettingsVariableValue: repoSettings,
    environmentSettingsVariableValue: '',
    environmentName: '',
    customSettings: '',
  };
}

// ── Settings reading logic (port of C# ReadALGoSettings) ──────────────────────

export async function readSettings(options: ReadSettingsOptions): Promise<Record<string, unknown>> {
  if (!options.baseFolder) {
    throw new Error('baseFolder is required');
  }

  const repoName = normalizeRepoName(options.repoName);
  const workflowName = sanitizeWorkflowName(options.workflowName);
  const settings = getDefaultSettings(repoName);

  const sources = await buildSettingsSources(options, workflowName);

  for (const [, sourceSettings] of sources) {
    if (!sourceSettings) { continue; }

    mergeInto(settings, sourceSettings);

    const conditionalSettings = getPropertyIgnoreCase(sourceSettings, 'ConditionalSettings');
    if (Array.isArray(conditionalSettings)) {
      for (const entry of conditionalSettings) {
        if (typeof entry === 'object' && entry !== null
          && isConditionalMatch(entry, options, repoName, workflowName)) {
          const condSettings = getPropertyIgnoreCase(entry, 'settings');
          if (condSettings && typeof condSettings === 'object' && !Array.isArray(condSettings)) {
            mergeInto(settings, condSettings as Record<string, unknown>);
          }
        }
      }
    }
  }

  postProcessSettings(settings, options.project);
  return settings;
}

function sanitizeWorkflowName(workflowName: string): string {
  if (!workflowName.trim()) { return ''; }
  // Remove characters invalid in file names
  return workflowName.trim().replace(/[<>:"/\\|?*\x00-\x1f]/g, '');
}

function normalizeRepoName(repoName: string): string {
  if (!repoName) { return ''; }
  const idx = repoName.lastIndexOf('/');
  return idx >= 0 ? repoName.substring(idx + 1) : repoName;
}

function getPropertyIgnoreCase(obj: Record<string, unknown>, key: string): unknown {
  if (key in obj) { return obj[key]; }
  const found = Object.keys(obj).find(k => k.toLowerCase() === key.toLowerCase());
  return found ? obj[found] : undefined;
}

function getExistingKeyIgnoreCase(obj: Record<string, unknown>, key: string): string | undefined {
  if (key in obj) { return key; }
  return Object.keys(obj).find(k => k.toLowerCase() === key.toLowerCase());
}

async function uriExists(uri: vscode.Uri): Promise<boolean> {
  try {
    await vscode.workspace.fs.stat(uri);
    return true;
  } catch {
    return false;
  }
}


async function readSettingsFile(uri: vscode.Uri): Promise<Record<string, unknown> | undefined> {
  if (!await uriExists(uri)) { return undefined; }
  try {
    const bytes = await vscode.workspace.fs.readFile(uri);
    const text = new TextDecoder('utf-8').decode(bytes);
    if (!text.trim()) { return undefined; }
    return JSON.parse(text);
  } catch (err) {
    throw new Error(`Error reading ${uri.toString()}: ${err instanceof Error ? err.message : String(err)}`);
  }
}

function parseJsonObject(json: string, sourceName: string): Record<string, unknown> {
  try {
    const parsed = JSON.parse(json);
    if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) {
      throw new Error(`${sourceName} does not contain a JSON object.`);
    }
    return parsed;
  } catch (err) {
    throw new Error(`Failed to parse JSON from ${sourceName}: ${err instanceof Error ? err.message : String(err)}`);
  }
}

async function buildSettingsSources(options: ReadSettingsOptions, workflowName: string): Promise<[string, Record<string, unknown> | undefined][]> {
  const result: [string, Record<string, unknown> | undefined][] = [];
  const githubFolder = vscode.Uri.joinPath(options.baseFolder, '.github');

  if (options.orgSettingsVariableValue) {
    result.push(['ALGoOrgSettings', parseJsonObject(options.orgSettingsVariableValue, 'ALGoOrgSettings')]);
  }

  result.push([CustomTemplateRepoSettingsPath.join('/'), await readSettingsFile(vscode.Uri.joinPath(options.baseFolder, ...CustomTemplateRepoSettingsPath))]);
  result.push([RepoSettingsPath.join('/'), await readSettingsFile(vscode.Uri.joinPath(options.baseFolder, ...RepoSettingsPath))]);

  if (options.repoSettingsVariableValue) {
    result.push(['ALGoRepoSettings', parseJsonObject(options.repoSettingsVariableValue, 'ALGoRepoSettings')]);
  }

  let projectFolder: vscode.Uri | undefined;
  if (options.project) {
    projectFolder = vscode.Uri.joinPath(options.baseFolder, options.project);
    result.push([CustomTemplateProjectSettingsPath.join('/'), await readSettingsFile(vscode.Uri.joinPath(options.baseFolder, ...CustomTemplateProjectSettingsPath))]);
    result.push([`${options.project}/${ALGoSettingsPath.join('/')}`, await readSettingsFile(vscode.Uri.joinPath(projectFolder, ...ALGoSettingsPath))]);
  }

  if (workflowName) {
    result.push([`.github/${workflowName}.settings.json`, await readSettingsFile(vscode.Uri.joinPath(githubFolder, `${workflowName}.settings.json`))]);

    if (projectFolder) {
      result.push([`${options.project}/${ALGoFolderName}/${workflowName}.settings.json`,
        await readSettingsFile(vscode.Uri.joinPath(projectFolder, ALGoFolderName, `${workflowName}.settings.json`))]);

      if (options.userName) {
        result.push([`${options.project}/${ALGoFolderName}/${options.userName}.settings.json`,
          await readSettingsFile(vscode.Uri.joinPath(projectFolder, ALGoFolderName, `${options.userName}.settings.json`))]);
      }
    }
  }

  if (options.environmentSettingsVariableValue) {
    result.push([`ALGoEnvSettings for ${options.environmentName}`, parseJsonObject(options.environmentSettingsVariableValue, 'ALGoEnvSettings')]);
  }

  if (options.customSettings) {
    result.push(['CustomSettings', parseJsonObject(options.customSettings, 'customSettings')]);
  }

  return result;
}

function mergeInto(destination: Record<string, unknown>, source: Record<string, unknown>): void {
  const overwriteSettings = getPropertyIgnoreCase(source, 'overwriteSettings');
  if (Array.isArray(overwriteSettings)) {
    for (const item of overwriteSettings) {
      if (typeof item !== 'string' || !item) { continue; }
      const destKey = getExistingKeyIgnoreCase(destination, item);
      const srcKey = getExistingKeyIgnoreCase(source, item);
      if (destKey && srcKey) {
        delete destination[destKey];
      }
    }
  }

  for (const [key, value] of Object.entries(source)) {
    if (key === 'overwriteSettings') { continue; }

    const destKey = getExistingKeyIgnoreCase(destination, key) ?? key;
    const dstValue = destination[destKey];

    if (dstValue === undefined || dstValue === null) {
      destination[destKey] = structuredClone(value);
      continue;
    }

    if (isPlainObject(dstValue) && isPlainObject(value)) {
      mergeInto(dstValue as Record<string, unknown>, value as Record<string, unknown>);
      continue;
    }

    if (Array.isArray(dstValue) && Array.isArray(value)) {
      mergeArrays(dstValue, value);
      continue;
    }

    destination[destKey] = structuredClone(value);
  }
}

function mergeArrays(destination: unknown[], source: unknown[]): void {
  for (const item of source) {
    if (isPlainObject(item)) {
      destination.push(structuredClone(item));
      continue;
    }
    const exists = destination.some(d => JSON.stringify(d) === JSON.stringify(item));
    if (!exists) {
      destination.push(structuredClone(item));
    }
  }
}

function isPlainObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function isConditionalMatch(entry: Record<string, unknown>, options: ReadSettingsOptions, repoName: string, workflowName: string): boolean {
  const checks: [string, string][] = [
    ['buildModes', options.buildMode],
    ['branches', options.branchName],
    ['repositories', repoName],
    ['projects', options.project],
    ['workflows', workflowName],
    ['users', options.userName],
  ];

  for (const [key, value] of checks) {
    const patterns = getPropertyIgnoreCase(entry, key);
    if (!Array.isArray(patterns)) { continue; }

    const patternsToMatch = key === 'workflows'
      ? patterns.map(p => sanitizeWorkflowName(String(p ?? '')))
      : patterns.map(p => String(p ?? ''));

    if (!value) { return false; }

    const anyMatch = patternsToMatch.some(pattern => wildcardLike(value, pattern));
    if (!anyMatch) { return false; }
  }

  return true;
}

function wildcardLike(value: string, pattern: string): boolean {
  if (!pattern) { return false; }
  const escaped = pattern.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const regex = '^' + escaped.replace(/\\\*/g, '.*').replace(/\\\?/g, '.') + '$';
  return new RegExp(regex, 'i').test(value ?? '');
}

function getString(obj: Record<string, unknown>, key: string, fallback = ''): string {
  const value = getPropertyIgnoreCase(obj, key);
  if (value === undefined || value === null) { return fallback; }
  if (typeof value === 'string') { return value; }
  return String(value);
}

function postProcessSettings(settings: Record<string, unknown>, project: string): void {
  const runsOn = getString(settings, 'runs-on');
  let shell = getString(settings, 'shell');
  let githubRunner = getString(settings, 'githubRunner');
  let githubRunnerShell = getString(settings, 'githubRunnerShell');

  if (!shell) {
    shell = runsOn.toLowerCase().includes('ubuntu-') ? 'pwsh' : 'powershell';
  }

  if (!githubRunner) {
    githubRunner = runsOn.toLowerCase().includes('ubuntu-') ? 'windows-latest' : runsOn;
  }

  if (!githubRunnerShell) {
    githubRunnerShell = shell;
  }

  if (githubRunnerShell.toLowerCase() !== 'powershell' && githubRunnerShell.toLowerCase() !== 'pwsh') {
    throw new Error(`Invalid value for setting: gitHubRunnerShell: ${githubRunnerShell}`);
  }

  if (shell.toLowerCase() !== 'powershell' && shell.toLowerCase() !== 'pwsh') {
    throw new Error(`Invalid value for setting: shell: ${shell}`);
  }

  if (githubRunner.toLowerCase().includes('ubuntu-') && githubRunnerShell.toLowerCase() === 'powershell') {
    githubRunnerShell = 'pwsh';
  }

  settings['shell'] = shell;
  settings['githubRunner'] = githubRunner;
  settings['githubRunnerShell'] = githubRunnerShell;

  if (!getString(settings, 'projectName')) {
    settings['projectName'] = project;
  }

  // Resolve artifact country: if the artifact shorthand omits the country segment, inject it
  // from the 'country' setting.
  const artifact = getString(settings, 'artifact');
  const country = getString(settings, 'country');
  if (artifact && !artifact.startsWith('https://') && country) {
    // Shorthand format: storageAccount/type/version/country/select
    // Pad with extra slashes so split always yields at least 5 parts
    const segments = `${artifact}/////`.split('/');
    if (!segments[3]) {
      settings['artifact'] = [segments[0], segments[1], segments[2], country, segments[4]].join('/').replace(/\/+$/, '');
    }
  }
}

function getDefaultSettings(repoName: string): Record<string, unknown> {
  return {
    type: 'PTE',
    unusedALGoSystemFiles: [],
    projects: [],
    powerPlatformSolutionFolder: '',
    country: 'us',
    artifact: '',
    companyName: '',
    repoVersion: '1.0',
    repoName: repoName,
    versioningStrategy: 0,
    runNumberOffset: 0,
    appBuild: 0,
    appRevision: 0,
    keyVaultName: '',
    licenseFileUrlSecretName: 'licenseFileUrl',
    ghTokenWorkflowSecretName: 'ghTokenWorkflow',
    adminCenterApiCredentialsSecretName: 'adminCenterApiCredentials',
    applicationInsightsConnectionStringSecretName: 'applicationInsightsConnectionString',
    keyVaultCertificateUrlSecretName: 'keyVaultCertificateUrl',
    keyVaultCertificatePasswordSecretName: 'keyVaultCertificatePassword',
    keyVaultClientIdSecretName: 'keyVaultClientId',
    keyVaultCodesignCertificateName: '',
    codeSignCertificateUrlSecretName: 'codeSignCertificateUrl',
    codeSignCertificatePasswordSecretName: 'codeSignCertificatePassword',
    additionalCountries: [],
    appDependencies: [],
    projectName: '',
    appFolders: [],
    testDependencies: [],
    testFolders: [],
    bcptTestFolders: [],
    pageScriptingTests: [],
    restoreDatabases: [],
    installApps: [],
    installTestApps: [],
    installOnlyReferencedApps: true,
    runTestsInAllInstalledTestApps: false,
    generateDependencyArtifact: false,
    skipUpgrade: false,
    applicationDependency: '18.0.0.0',
    updateDependencies: false,
    installTestRunner: false,
    installTestFramework: false,
    installTestLibraries: false,
    installPerformanceToolkit: false,
    enableCodeCop: false,
    enableUICop: false,
    enableCodeAnalyzersOnTestApps: false,
    customCodeCops: [],
    trackALAlertsInGitHub: false,
    failOn: 'error',
    treatTestFailuresAsWarnings: false,
    rulesetFile: '',
    enableExternalRulesets: false,
    vsixFile: '',
    assignPremiumPlan: false,
    enableTaskScheduler: false,
    doNotBuildTests: false,
    doNotRunTests: false,
    doNotRunBcptTests: false,
    doNotRunPageScriptingTests: false,
    doNotPublishApps: false,
    doNotSignApps: false,
    configPackages: [],
    appSourceCopMandatoryAffixes: [],
    deliverToAppSource: {
      mainAppFolder: '',
      productId: '',
      includeDependencies: [],
      continuousDelivery: false,
    },
    obsoleteTagMinAllowedMajorMinor: '',
    memoryLimit: '',
    templateUrl: '',
    templateSha: '',
    templateBranch: '',
    appDependencyProbingPaths: [],
    useProjectDependencies: false,
    'runs-on': 'windows-latest',
    shell: '',
    githubRunner: '',
    githubRunnerShell: '',
    cacheImageName: 'my',
    cacheKeepDays: 3,
    alwaysBuildAllProjects: false,
    incrementalBuilds: {
      onPush: false,
      onPull_Request: true,
      onSchedule: false,
      retentionDays: 30,
      mode: 'modifiedApps',
    },
    microsoftTelemetryConnectionString: 'InstrumentationKey=cd2cc63e-0f37-4968-b99a-532411a314b8;IngestionEndpoint=https://northeurope-2.in.applicationinsights.azure.com/',
    partnerTelemetryConnectionString: '',
    sendExtendedTelemetryToMicrosoft: false,
    environments: [],
    buildModes: [],
    useCompilerFolder: false,
    pullRequestTrigger: 'pull_request',
    bcptThresholds: {
      DurationWarning: 10,
      DurationError: 25,
      NumberOfSqlStmtsWarning: 5,
      NumberOfSqlStmtsError: 10,
    },
    fullBuildPatterns: [],
    excludeEnvironments: [],
    alDoc: {
      continuousDeployment: false,
      deployToGitHubPages: true,
      maxReleases: 3,
      groupByProject: true,
      includeProjects: [],
      excludeProjects: [],
      header: 'Documentation for {REPOSITORY} {VERSION}',
      footer: 'Documentation for <a href="https://github.com/{REPOSITORY}">{REPOSITORY}</a> made with <a href="https://aka.ms/AL-Go">AL-Go for GitHub</a>, <a href="https://go.microsoft.com/fwlink/?linkid=2247728">ALDoc</a> and <a href="https://dotnet.github.io/docfx">DocFx</a>',
      defaultIndexMD: '## Reference documentation\n\nThis is the generated reference documentation for [{REPOSITORY}](https://github.com/{REPOSITORY}).\n\nYou can use the navigation bar at the top and the table of contents to the left to navigate your documentation.\n\nYou can change this content by creating/editing the **{INDEXTEMPLATERELATIVEPATH}** file in your repository or use the alDoc:defaultIndexMD setting in your repository settings file (.github/AL-Go-Settings.json)\n\n{RELEASENOTES}',
      defaultReleaseMD: '## Release reference documentation\n\nThis is the generated reference documentation for [{REPOSITORY}](https://github.com/{REPOSITORY}).\n\nYou can use the navigation bar at the top and the table of contents to the left to navigate your documentation.\n\nYou can change this content by creating/editing the **{INDEXTEMPLATERELATIVEPATH}** file in your repository or use the alDoc:defaultReleaseMD setting in your repository settings file (.github/AL-Go-Settings.json)\n\n{RELEASENOTES}',
    },
    trustMicrosoftNuGetFeeds: true,
    nuGetFeedSelectMode: 'LatestMatching',
    commitOptions: {
      messageSuffix: '',
      pullRequestAutoMerge: false,
      pullRequestMergeMethod: 'squash',
      pullRequestLabels: [],
      createPullRequest: true,
    },
    trustedSigning: {
      Endpoint: '',
      Account: '',
      CertificateProfile: '',
    },
    useGitSubmodules: 'false',
    gitSubmodulesTokenSecretName: 'gitSubmodulesToken',
    shortLivedArtifactsRetentionDays: 1,
    reportSuppressedDiagnostics: false,
    workflowDefaultInputs: [],
    customALGoFiles: {
      filesToInclude: [],
      filesToExclude: [],
    },
    postponeProjectInBuildOrder: false,
  };
}
