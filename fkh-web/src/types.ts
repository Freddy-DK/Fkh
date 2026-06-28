/** Mirrors the backend's FunctionCatalogResponse / ContainerInfo shapes. */

export interface FunctionParameterDefinition {
  name: string;
  type: string;
  description: string;
  required: boolean;
  adminOnly: boolean;
  defaultValue: string | null;
}

export interface FunctionDefinition {
  name: string;
  description: string;
  route: string;
  parameters: FunctionParameterDefinition[];
  adminOnly: boolean;
}

export interface FunctionCatalogResponse {
  functions: FunctionDefinition[];
}

export interface CurrentUserResponse {
  username: string;
  isAdmin: boolean;
  isSupport: boolean;
}

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
  database?: string;
  tenantDatabase?: string;
  multitenant?: boolean;
  devScope?: boolean;
  auth?: string;
}

export interface ListContainersResponse {
  containers: ContainerInfo[];
}

export interface GitHubUser {
  login: string;
  avatar_url: string;
  name: string | null;
}
