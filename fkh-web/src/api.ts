import type { FunctionCatalogResponse, ListContainersResponse } from './types';

const PROTOCOL_VERSION = '1';
const CLIENT_APP = 'Web App';

/** Thrown when the backend returns 503 indicating the AKS cluster is stopped. */
export class SystemStoppedError extends Error {
  constructor(message?: string) {
    super(message ?? 'The system is currently stopped.');
    this.name = 'SystemStoppedError';
  }
}

/** Resolve the Fkh backend URL.
 *  Priority: ?backendUrl= query param > build-time VITE_BACKEND_URL > localhost fallback.
 */
export function resolveBackendUrl(): string {
  const params = new URLSearchParams(window.location.search);
  const explicit = params.get('backendUrl');
  if (explicit) return explicit.replace(/\/+$/, '');

  // Build-time default (baked in by CI/CD)
  const buildTime = import.meta.env.VITE_BACKEND_URL;
  if (buildTime) return buildTime.replace(/\/+$/, '');

  // Local dev
  const host = window.location.hostname;
  if (host === 'localhost' || host === '127.0.0.1') {
    return 'http://localhost:7071/api';
  }

  return '';
}

/** Extract the org name from a backend URL (e.g. fkh-myorg-backend → myorg) */
export function getOrgNameFromUrl(url: string): string {
  const match = url.match(/fkh-(.+?)-backend/);
  return match ? match[1] ?? '' : '';
}

async function apiFetch(backendUrl: string, path: string, token: string, body?: Record<string, unknown>): Promise<Response> {
  const url = `${backendUrl}/${path}`;
  const headers: Record<string, string> = {
    Authorization: `Bearer ${token}`,
    'X-Fkh-Protocol-Version': PROTOCOL_VERSION,
    'X-Fkh-Client': CLIENT_APP,
  };
  if (body) {
    headers['Content-Type'] = 'application/json';
  }
  return fetch(url, {
    method: body ? 'POST' : 'GET',
    headers,
    body: body ? JSON.stringify(body) : undefined,
  });
}

export async function fetchFunctionCatalog(backendUrl: string): Promise<FunctionCatalogResponse> {
  const res = await fetch(`${backendUrl}/functions`, { method: 'GET' });
  if (!res.ok) throw new Error(`Failed to fetch catalog: ${res.status}`);
  return (await res.json()) as FunctionCatalogResponse;
}

export async function listContainers(backendUrl: string, token: string, all: boolean): Promise<ListContainersResponse> {
  const params: Record<string, string> = {
    _timezone: Intl.DateTimeFormat().resolvedOptions().timeZone,
  };
  if (all) params['all'] = 'true';

  const res = await apiFetch(backendUrl, 'ListContainers', token, { parameters: params });
  if (res.status === 503) throw new SystemStoppedError();
  if (!res.ok) {
    const text = await res.text();
    throw new Error(`ListContainers failed (${res.status}): ${text}`);
  }
  return (await res.json()) as ListContainersResponse;
}

/** Invoke a backend function and handle 202 retry polling. */
export async function invokeFunction(
  backendUrl: string,
  token: string,
  route: string,
  parameters: Record<string, string>,
  onRetry?: (message: string) => void,
): Promise<Record<string, unknown>> {
  parameters['_timezone'] = Intl.DateTimeFormat().resolvedOptions().timeZone;

  let res = await apiFetch(backendUrl, route, token, { parameters });

  // Poll on 202 Accepted
  while (res.status === 202) {
    const result = (await res.json()) as { message?: string; retryAfterSeconds?: number };
    const delay = (result.retryAfterSeconds ?? 10) * 1000;
    onRetry?.(result.message ?? 'Working...');
    await new Promise(r => setTimeout(r, delay));
    res = await apiFetch(backendUrl, route, token, { parameters });
  }

  if (res.status === 503) throw new SystemStoppedError();
  if (!res.ok) {
    const text = await res.text();
    throw new Error(`${route} failed (${res.status}): ${text}`);
  }
  return (await res.json()) as Record<string, unknown>;
}

/** Start the AKS cluster. Handles 202 retry polling. */
export async function startFkh(
  backendUrl: string,
  token: string,
  onRetry?: (message: string) => void,
): Promise<Record<string, unknown>> {
  return invokeFunction(backendUrl, token, 'StartFkh', {}, onRetry);
}
