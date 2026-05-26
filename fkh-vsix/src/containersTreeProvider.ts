import * as vscode from 'vscode';
import { getProjects } from './readALGoSettings';
import { getProtocolHeaders } from './protocol';

// ── AL-Go Projects tree ──────────────────────────────────────────────────────

export class ProjectTreeItem extends vscode.TreeItem {
  constructor(
    public readonly label: string,
    public readonly collapsibleState: vscode.TreeItemCollapsibleState,
    public readonly projectName?: string,
    public readonly containerInfo?: ContainerInfo,
    public readonly isProperty?: boolean,
  ) {
    super(label, collapsibleState);
  }
}

export class ProjectsTreeProvider implements vscode.TreeDataProvider<ProjectTreeItem> {
  private _onDidChangeTreeData = new vscode.EventEmitter<ProjectTreeItem | undefined>();
  readonly onDidChangeTreeData = this._onDidChangeTreeData.event;

  private projects: string[] = [];
  private initialized = false;
  private _getRepoName: () => string;
  private _getContainers: () => ContainerInfo[];

  constructor(getRepoName: () => string, getContainers: () => ContainerInfo[]) {
    this._getRepoName = getRepoName;
    this._getContainers = getContainers;
  }

  async refresh(): Promise<void> {
    this.projects = await getProjects();
    this.initialized = true;
    this._onDidChangeTreeData.fire(undefined);
  }

  getTreeItem(element: ProjectTreeItem): vscode.TreeItem {
    return element;
  }

  getChildren(element?: ProjectTreeItem): ProjectTreeItem[] {
    if (!element) {
      if (!this.initialized) { return []; }

      if (this.projects.length === 0) {
        const empty = new ProjectTreeItem('No AL-Go projects found', vscode.TreeItemCollapsibleState.None);
        empty.iconPath = new vscode.ThemeIcon('info');
        return [empty];
      }

      return this.projects.map(name => {
        const containers = this.getContainersForProject(name);
        const state = containers.length > 0
          ? vscode.TreeItemCollapsibleState.Expanded
          : vscode.TreeItemCollapsibleState.None;
        const item = new ProjectTreeItem(name, state, name);
        item.iconPath = new vscode.ThemeIcon('symbol-folder');
        item.contextValue = 'algoProject';
        if (containers.length > 0) {
          item.description = `${containers.length} container${containers.length !== 1 ? 's' : ''}`;
        }
        return item;
      });
    }

    // Children of a project: containers
    if (element.projectName && !element.containerInfo) {
      const containers = this.getContainersForProject(element.projectName);
      return containers.map(container => {
        const statusLower = container.status.toLowerCase();
        const icon = statusLower.startsWith('running')
          ? new vscode.ThemeIcon('vm-running', new vscode.ThemeColor('testing.iconPassed'))
          : statusLower.startsWith('starting')
            ? new vscode.ThemeIcon('sync~spin', new vscode.ThemeColor('testing.iconQueued'))
            : new vscode.ThemeIcon('vm', new vscode.ThemeColor('testing.iconSkipped'));

        const item = new ProjectTreeItem(
          `${container.name} (${container.status})`,
          vscode.TreeItemCollapsibleState.Collapsed,
          undefined,
          container
        );
        item.iconPath = icon;
        item.tooltip = `${container.appLabel}\nStatus: ${container.status}`;
        item.contextValue = `container-${container.status.toLowerCase()}`;
        return item;
      });
    }

    // Children of a container: properties
    if (element.containerInfo) {
      return buildContainerPropertyNodes(element.containerInfo, ProjectTreeItem);
    }

    return [];
  }

  private getContainersForProject(projectName: string): ContainerInfo[] {
    const repoName = this._getRepoName();
    if (!repoName) { return []; }
    const containers = this._getContainers();
    return containers.filter(container => {
      const containerRepo = container.repo ?? '';
      const containerProject = container.project ?? '';
      return containerRepo.toLowerCase() === repoName.toLowerCase()
        && containerProject.toLowerCase() === projectName.toLowerCase();
    });
  }
}

// ── Containers tree ──────────────────────────────────────────────────────────

export interface ContainerInfo {
  appLabel: string;
  name: string;
  status: string;
  statusDetail: string;
  reason?: string;
  image: string;
  autoStop?: string;
  repo?: string;
  project?: string;
  webClient?: string;
  memory?: string;
}

function parseContainerLabels(containers: string | string[] | undefined): string[] {
  if (!containers) { return []; }
  if (Array.isArray(containers)) { return containers.map(s => s.trim()); }
  return containers.split(',').map(s => s.trim());
}

function buildContainerPropertyNodes<T extends vscode.TreeItem>(
  info: ContainerInfo,
  Ctor: new (label: string, state: vscode.TreeItemCollapsibleState, ...args: any[]) => T,
): T[] {
  const nodes: T[] = [];
  const add = (label: string, value: string | undefined, icon: string, command?: vscode.Command) => {
    if (!value) { return; }
    const node = new Ctor(`${label}: ${value}`, vscode.TreeItemCollapsibleState.None);
    node.tooltip = `${label}: ${value}`;
    node.iconPath = new vscode.ThemeIcon(icon);
    if (command) { node.command = command; }
    nodes.push(node);
  };

  add('Status', info.statusDetail, 'info');
  if (info.reason) { add('Reason', info.reason, 'warning'); }
  add('Image', info.image, 'package');
  add('AutoStop', info.autoStop, 'watch');
  add('Repo', info.repo, 'repo');
  add('Project', info.project, 'symbol-folder');
  if (info.webClient) {
    add('WebClient', info.webClient, 'link-external', {
      command: 'vscode.open',
      title: 'Open WebClient',
      arguments: [vscode.Uri.parse(info.webClient)],
    });
  }
  add('Memory', info.memory, 'server-process');
  return nodes;
}

export class ContainerTreeItem extends vscode.TreeItem {
  constructor(
    public readonly label: string,
    public readonly collapsibleState: vscode.TreeItemCollapsibleState,
    public readonly containerInfo?: ContainerInfo,
  ) {
    super(label, collapsibleState);
  }
}

export class ContainersTreeProvider implements vscode.TreeDataProvider<ContainerTreeItem> {
  private _onDidChangeTreeData = new vscode.EventEmitter<ContainerTreeItem | undefined>();
  readonly onDidChangeTreeData = this._onDidChangeTreeData.event;

  private containers: ContainerInfo[] = [];
  private myContainers: ContainerInfo[] = [];
  private _initialized = false;
  private _showAll = false;
  private _connected = false;
  private _getBackendUrl: () => string | undefined;
  private _getGitHubSession: () => Promise<vscode.AuthenticationSession | undefined>;

  constructor(
    getBackendUrl: () => string | undefined,
    getGitHubSession: () => Promise<vscode.AuthenticationSession | undefined>
  ) {
    this._getBackendUrl = getBackendUrl;
    this._getGitHubSession = getGitHubSession;
  }

  getContainers(): ContainerInfo[] {
    return this.containers;
  }

  getMyContainers(): ContainerInfo[] {
    return this.myContainers;
  }

  get showAll(): boolean { return this._showAll; }
  set showAll(value: boolean) { this._showAll = value; }

  get initialized(): boolean { return this._initialized; }
  get connected(): boolean { return this._connected; }

  async refresh(): Promise<void> {
    const myResult = await this.fetchContainers(false);
    this._connected = myResult.connected;
    this.myContainers = myResult.containers;
    this.containers = this._showAll ? (await this.fetchContainers(true)).containers : this.myContainers;
    this._initialized = true;
    this._onDidChangeTreeData.fire(undefined);
  }

  getTreeItem(element: ContainerTreeItem): vscode.TreeItem {
    return element;
  }

  getChildren(element?: ContainerTreeItem): ContainerTreeItem[] {
    if (!element) {
      if (!this.initialized) { return []; }
      if (this.containers.length === 0) {
        const empty = new ContainerTreeItem('No containers', vscode.TreeItemCollapsibleState.None);
        empty.iconPath = new vscode.ThemeIcon('info');
        return [empty];
      }
      return this.containers.map(container => {
        const statusLower = container.status.toLowerCase();
        const icon = statusLower.startsWith('running')
          ? new vscode.ThemeIcon('vm-running', new vscode.ThemeColor('testing.iconPassed'))
          : statusLower.startsWith('starting')
            ? new vscode.ThemeIcon('sync~spin', new vscode.ThemeColor('testing.iconQueued'))
            : new vscode.ThemeIcon('vm', new vscode.ThemeColor('testing.iconSkipped'));

        const item = new ContainerTreeItem(
          `${this._showAll ? container.appLabel : container.name} (${container.status})`,
          vscode.TreeItemCollapsibleState.Collapsed,
          container
        );
        item.iconPath = icon;
        item.tooltip = `${container.appLabel}\nStatus: ${container.status}`;
        item.contextValue = `container-${container.status.toLowerCase()}`;
        return item;
      });
    }

    if (element.containerInfo) {
      return buildContainerPropertyNodes(element.containerInfo, ContainerTreeItem);
    }

    return [];
  }

  private async fetchContainers(showAll?: boolean): Promise<{ containers: ContainerInfo[]; connected: boolean }> {
    const session = await this._getGitHubSession();
    if (!session) { return { containers: [], connected: false }; }

    const baseUrl = this._getBackendUrl();
    if (!baseUrl) { return { containers: [], connected: false }; }

    try {
      const response = await fetch(`${baseUrl}/ListContainers`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          Authorization: `Bearer ${session.accessToken}`,
          ...getProtocolHeaders(),
        },
        body: JSON.stringify({ parameters: { _timezone: vscode.workspace.getConfiguration('fkh').get<string>('timezone', '').trim()
          || Intl.DateTimeFormat().resolvedOptions().timeZone, ...(showAll ? { all: 'true' } : {}) } }),
      });

      if (!response.ok) { return { containers: [], connected: false }; }

      const result = await response.json() as { containers: ContainerInfo[] };
      return { containers: result.containers ?? [], connected: true };
    } catch {
      return { containers: [], connected: false };
    }
  }
}

// ── Images tree ──────────────────────────────────────────────────────────────

export interface ImageInfo {
  repository: string;
  tags: { name: string; size: string; updated: string; lastUsed: string }[];
}

export class ImageTreeItem extends vscode.TreeItem {
  constructor(
    public readonly label: string,
    public readonly collapsibleState: vscode.TreeItemCollapsibleState,
    public readonly imageInfo?: ImageInfo,
    public readonly repositoryName?: string,
    public readonly tagName?: string,
  ) {
    super(label, collapsibleState);
  }
}

export class ImagesTreeProvider implements vscode.TreeDataProvider<ImageTreeItem> {
  private _onDidChangeTreeData = new vscode.EventEmitter<ImageTreeItem | undefined>();
  readonly onDidChangeTreeData = this._onDidChangeTreeData.event;

  private images: ImageInfo[] = [];
  private initialized = false;
  private _getBackendUrl: () => string | undefined;
  private _getGitHubSession: () => Promise<vscode.AuthenticationSession | undefined>;

  constructor(
    getBackendUrl: () => string | undefined,
    getGitHubSession: () => Promise<vscode.AuthenticationSession | undefined>
  ) {
    this._getBackendUrl = getBackendUrl;
    this._getGitHubSession = getGitHubSession;
  }

  async refresh(): Promise<void> {
    this.images = await this.fetchImages();
    this.initialized = true;
    this._onDidChangeTreeData.fire(undefined);
  }

  getTreeItem(element: ImageTreeItem): vscode.TreeItem {
    return element;
  }

  getChildren(element?: ImageTreeItem): ImageTreeItem[] {
    if (!element) {
      if (!this.initialized) { return []; }
      if (this.images.length === 0) {
        const empty = new ImageTreeItem('No images found', vscode.TreeItemCollapsibleState.None);
        empty.iconPath = new vscode.ThemeIcon('info');
        return [empty];
      }
      return this.images.map(img => {
        const item = new ImageTreeItem(
          img.repository,
          vscode.TreeItemCollapsibleState.Collapsed,
          img
        );
        item.iconPath = new vscode.ThemeIcon('package');
        item.tooltip = `${img.repository} (${img.tags.length} tag${img.tags.length !== 1 ? 's' : ''})`;
        item.contextValue = 'acrRepository';
        return item;
      });
    }

    if (element.imageInfo) {
      return element.imageInfo.tags.map(tag => {
        const child = new ImageTreeItem(
          `${tag.name}  (${tag.size}, ${tag.updated})`,
          vscode.TreeItemCollapsibleState.None,
          undefined,
          element.imageInfo!.repository,
          tag.name
        );
        child.iconPath = new vscode.ThemeIcon('tag');
        child.tooltip = `${tag.name}\nSize: ${tag.size}\nUpdated: ${tag.updated}\nLast used: ${tag.lastUsed}`;
        child.description = tag.lastUsed !== 'never' ? `last used: ${tag.lastUsed}` : undefined;
        child.contextValue = 'acrTag';
        return child;
      });
    }

    return [];
  }

  private async fetchImages(): Promise<ImageInfo[]> {
    const session = await this._getGitHubSession();
    if (!session) { return []; }

    const baseUrl = this._getBackendUrl();
    if (!baseUrl) { return []; }

    try {
      const response = await fetch(`${baseUrl}/ListImages`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          Authorization: `Bearer ${session.accessToken}`,
          ...getProtocolHeaders(),
        },
        body: JSON.stringify({ parameters: {} }),
      });

      if (!response.ok) { return []; }

      const result = await response.json() as { images: ImageInfo[] };
      return result.images ?? [];
    } catch {
      return [];
    }
  }
}

// ── Nodes tree (admin only) ──────────────────────────────────────────────────

export interface VMInfo {
  name: string;
  status: string;
  containers: string | string[];
  cns: string;
  cpu: string;
  memory: string;
  kubelet: string;
  os: string;
}

export class VMTreeItem extends vscode.TreeItem {
  constructor(
    public readonly label: string,
    public readonly collapsibleState: vscode.TreeItemCollapsibleState,
    public readonly vmInfo?: VMInfo,
    public readonly containerInfo?: ContainerInfo,
    public readonly isVMInfoGroup?: boolean,
  ) {
    super(label, collapsibleState);
  }
}

export class VMsTreeProvider implements vscode.TreeDataProvider<VMTreeItem> {
  private _onDidChangeTreeData = new vscode.EventEmitter<VMTreeItem | undefined>();
  readonly onDidChangeTreeData = this._onDidChangeTreeData.event;

  private vms: VMInfo[] = [];
  private initialized = false;
  private _visible = false;
  private _getBackendUrl: () => string | undefined;
  private _getGitHubSession: () => Promise<vscode.AuthenticationSession | undefined>;
  private _getContainers: () => ContainerInfo[];

  constructor(
    getBackendUrl: () => string | undefined,
    getGitHubSession: () => Promise<vscode.AuthenticationSession | undefined>,
    getContainers: () => ContainerInfo[]
  ) {
    this._getBackendUrl = getBackendUrl;
    this._getGitHubSession = getGitHubSession;
    this._getContainers = getContainers;
  }

  get visible(): boolean { return this._visible; }

  async refresh(): Promise<void> {
    const result = await this.fetchVMs();
    this.vms = result.vms;
    this._visible = result.visible;
    this.initialized = true;
    this._onDidChangeTreeData.fire(undefined);
  }

  getTreeItem(element: VMTreeItem): vscode.TreeItem {
    return element;
  }

  getChildren(element?: VMTreeItem): VMTreeItem[] {
    if (!element) {
      if (!this.initialized) { return []; }
      if (this.vms.length === 0) {
        const empty = new VMTreeItem('No Windows VMs found', vscode.TreeItemCollapsibleState.None);
        empty.iconPath = new vscode.ThemeIcon('info');
        return [empty];
      }
      return this.vms.map(vm => {
        const statusLower = vm.status.toLowerCase();
        const icon = statusLower === 'ready'
          ? new vscode.ThemeIcon('vm-running', new vscode.ThemeColor('testing.iconPassed'))
          : new vscode.ThemeIcon('vm', new vscode.ThemeColor('testing.iconFailed'));

        const item = new VMTreeItem(
          `${vm.name} (${vm.status})`,
          vscode.TreeItemCollapsibleState.Collapsed,
          vm
        );
        item.iconPath = icon;
        item.tooltip = `${vm.name}\nStatus: ${vm.status}`;
        item.contextValue = 'windowsVM';
        const allContainers = this._getContainers();
        const vmContainerLabels = new Set(parseContainerLabels(vm.containers));
        const matchedContainerCount = allContainers.filter(p => vmContainerLabels.has(p.appLabel)).length;
        if (matchedContainerCount > 0) {
          item.description = `${matchedContainerCount} container${matchedContainerCount !== 1 ? 's' : ''}`;
        }
        return item;
      });
    }

    // Children of a VM: "VM Info" group + containers
    if (element.vmInfo && !element.containerInfo && !element.isVMInfoGroup) {
      const children: VMTreeItem[] = [];

      // Collapsible "VM Info" group
      const infoItem = new VMTreeItem(
        'VM Info',
        vscode.TreeItemCollapsibleState.Collapsed,
        element.vmInfo,
        undefined,
        true
      );
      infoItem.iconPath = new vscode.ThemeIcon('info');
      children.push(infoItem);

      // Containers on this VM
      const allContainers = this._getContainers();
      const vmContainerLabels = new Set(parseContainerLabels(element.vmInfo.containers));
      const vmContainers = allContainers.filter(p => vmContainerLabels.has(p.appLabel));
      for (const container of vmContainers) {
        const statusLower = container.status.toLowerCase();
        const containerIcon = statusLower.startsWith('running')
          ? new vscode.ThemeIcon('vm-running', new vscode.ThemeColor('testing.iconPassed'))
          : statusLower.startsWith('starting')
            ? new vscode.ThemeIcon('sync~spin', new vscode.ThemeColor('testing.iconQueued'))
            : new vscode.ThemeIcon('vm', new vscode.ThemeColor('testing.iconSkipped'));

        const containerItem = new VMTreeItem(
          `${container.name} (${container.status})`,
          vscode.TreeItemCollapsibleState.Collapsed,
          undefined,
          container
        );
        containerItem.iconPath = containerIcon;
        containerItem.tooltip = `${container.appLabel}\nStatus: ${container.status}`;
        containerItem.contextValue = `container-${container.status.toLowerCase()}`;
        children.push(containerItem);
      }

      return children;
    }

    // Children of the "VM Info" group: VM properties
    if (element.isVMInfoGroup && element.vmInfo) {
      const vm = element.vmInfo;
      const nodes: VMTreeItem[] = [];
      const add = (label: string, value: string | undefined, icon: string, iconColor?: vscode.ThemeColor) => {
        if (!value) { return; }
        const node = new VMTreeItem(`${label}: ${value}`, vscode.TreeItemCollapsibleState.None);
        node.tooltip = `${label}: ${value}`;
        node.iconPath = new vscode.ThemeIcon(icon, iconColor);
        nodes.push(node);
      };
      add('CNS', vm.cns, vm.cns === 'Ready' ? 'pass' : 'error',
        vm.cns === 'Ready' ? new vscode.ThemeColor('testing.iconPassed') : new vscode.ThemeColor('testing.iconFailed'));
      add('Containers', Array.isArray(vm.containers) ? vm.containers.join(', ') : vm.containers, 'vm');
      add('CPU', vm.cpu, 'dashboard');
      add('Memory', vm.memory, 'server-process');
      add('Kubelet', vm.kubelet, 'versions');
      add('OS', vm.os, 'device-desktop');
      return nodes;
    }

    // Children of a container under a VM: properties
    if (element.containerInfo) {
      return buildContainerPropertyNodes(element.containerInfo, VMTreeItem);
    }

    return [];
  }

  private async fetchVMs(): Promise<{ vms: VMInfo[]; visible: boolean }> {
    const session = await this._getGitHubSession();
    if (!session) { return { vms: [], visible: false }; }

    const baseUrl = this._getBackendUrl();
    if (!baseUrl) { return { vms: [], visible: false }; }

    try {
      const response = await fetch(`${baseUrl}/ListVMs`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          Authorization: `Bearer ${session.accessToken}`,
          ...getProtocolHeaders(),
        },
        body: JSON.stringify({ parameters: {} }),
      });

      if (response.status === 403 || response.status === 500) {
        // Not admin — hide the view
        return { vms: [], visible: false };
      }

      if (!response.ok) { return { vms: [], visible: false }; }

      const result = await response.json() as { vms: VMInfo[] };
      return { vms: result.vms ?? [], visible: true };
    } catch {
      return { vms: [], visible: false };
    }
  }
}
