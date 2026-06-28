import { useState, useEffect, useCallback } from 'react';
import type { GitHubUser, ContainerInfo } from './types';
import { getStoredToken, storeToken, clearToken, validateToken } from './auth';
import { resolveBackendUrl, getOrgNameFromUrl, listContainers, invokeFunction, getCurrentUser, SystemStoppedError, AuthorizationError } from './api';
import { Login } from './components/Login.tsx';
import { Header } from './components/Header.tsx';
import { ContainerList } from './components/ContainerList.tsx';
import { SystemStopped } from './components/SystemStopped.tsx';

/** Read clientId: ?clientId= query param overrides the build-time default. */
function getClientId(): string {
  return new URLSearchParams(window.location.search).get('clientId')
    ?? import.meta.env.VITE_GITHUB_CLIENT_ID
    ?? '';
}

export function App() {
  const [user, setUser] = useState<GitHubUser | null>(null);
  const [token, setToken] = useState<string | null>(null);
  const [checking, setChecking] = useState(true);
  const [loginMessage, setLoginMessage] = useState<string | null>(null);

  const [backendUrl] = useState(() => resolveBackendUrl());
  const orgName = backendUrl ? getOrgNameFromUrl(backendUrl) : '';

  const [containers, setContainers] = useState<ContainerInfo[]>([]);
  const [containersLoading, setContainersLoading] = useState(false);
  const [containersError, setContainersError] = useState<string | null>(null);
  const [showAll, setShowAll] = useState(false);
  const [isAdmin, setIsAdmin] = useState(false);
  const [actionInProgress, setActionInProgress] = useState<string | null>(null);
  const [systemStopped, setSystemStopped] = useState(false);

  // Check for existing token on mount
  useEffect(() => {
    const stored = getStoredToken();
    if (stored) {
      validateToken(stored).then(u => {
        if (u) {
          setUser(u);
          setToken(stored);
        } else {
          clearToken();
        }
        setChecking(false);
      });
    } else {
      setChecking(false);
    }
  }, []);

  // Handle successful authentication
  const handleToken = useCallback(async (newToken: string) => {
    setLoginMessage(null);
    const u = await validateToken(newToken);
    if (u) {
      storeToken(newToken);
      setToken(newToken);
      setUser(u);
    } else {
      setLoginMessage('Invalid token - GitHub rejected it.');
    }
  }, []);

  const handleSignOut = useCallback(() => {
    clearToken();
    setToken(null);
    setUser(null);
    setContainers([]);
    setContainersError(null);
    setShowAll(false);
    setIsAdmin(false);
    setLoginMessage(null);
  }, []);

  const handleAuthFailure = useCallback((error: AuthorizationError) => {
    clearToken();
    setToken(null);
    setUser(null);
    setContainers([]);
    setContainersError(null);
    setShowAll(false);
    setIsAdmin(false);
    setLoginMessage(error.message);
  }, []);

  useEffect(() => {
    if (!token || !backendUrl) {
      setIsAdmin(false);
      return;
    }

    let cancelled = false;
    getCurrentUser(backendUrl, token)
      .then(currentUser => {
        if (cancelled) return;
        setIsAdmin(currentUser.isAdmin);
        if (!currentUser.isAdmin) {
          setShowAll(false);
        }
      })
      .catch(e => {
        if (cancelled) return;
        setIsAdmin(false);
        setShowAll(false);
        if (e instanceof AuthorizationError) {
          handleAuthFailure(e);
        } else {
          setContainersError(e instanceof Error ? e.message : 'Failed to load current user');
        }
      });

    return () => { cancelled = true; };
  }, [token, backendUrl, handleAuthFailure]);

  // Fetch containers
  const fetchContainers = useCallback(async (all?: boolean) => {
    if (!token || !backendUrl) return;
    setContainersLoading(true);
    setContainersError(null);
    try {
      const result = await listContainers(backendUrl, token, all ?? showAll);
      setSystemStopped(false);
      setContainers(result.containers ?? []);
    } catch (e) {
      if (e instanceof SystemStoppedError) {
        setSystemStopped(true);
      } else if (e instanceof AuthorizationError) {
        handleAuthFailure(e);
      } else {
        setContainersError(e instanceof Error ? e.message : 'Failed to load containers');
      }
      setContainers([]);
    } finally {
      setContainersLoading(false);
    }
  }, [token, backendUrl, showAll]);

  // Load containers once authenticated
  useEffect(() => {
    if (token && backendUrl) {
      fetchContainers();
    }
  }, [token, backendUrl, fetchContainers]);

  const handleToggleAll = useCallback(() => {
    if (!isAdmin) return;

    setShowAll(prev => {
      const next = !prev;
      // Refetch with new setting
      if (token && backendUrl) {
        setContainersLoading(true);
        setContainersError(null);
        listContainers(backendUrl, token, next)
          .then(r => setContainers(r.containers ?? []))
          .catch(e => {
            if (e instanceof AuthorizationError) {
              handleAuthFailure(e);
            } else {
              setContainersError(e instanceof Error ? e.message : 'Failed');
            }
          })
          .finally(() => setContainersLoading(false));
      }
      return next;
    });
  }, [token, backendUrl, handleAuthFailure, isAdmin]);

  const handleStart = useCallback(async (name: string) => {
    if (!token || !backendUrl) return;
    setActionInProgress(name);
    try {
      await invokeFunction(backendUrl, token, 'StartContainer', { name });
      await fetchContainers();
    } catch (e) {
      if (e instanceof SystemStoppedError) {
        setSystemStopped(true);
      } else if (e instanceof AuthorizationError) {
        handleAuthFailure(e);
      } else {
        setContainersError(e instanceof Error ? e.message : 'Start failed');
      }
    } finally {
      setActionInProgress(null);
    }
  }, [token, backendUrl, fetchContainers]);

  const handleStop = useCallback(async (name: string) => {
    if (!token || !backendUrl) return;
    setActionInProgress(name);
    try {
      await invokeFunction(backendUrl, token, 'StopContainer', { name });
      await fetchContainers();
    } catch (e) {
      if (e instanceof SystemStoppedError) {
        setSystemStopped(true);
      } else if (e instanceof AuthorizationError) {
        handleAuthFailure(e);
      } else {
        setContainersError(e instanceof Error ? e.message : 'Stop failed');
      }
    } finally {
      setActionInProgress(null);
    }
  }, [token, backendUrl, fetchContainers]);

  const handleStopFkh = useCallback(async () => {
    if (!token || !backendUrl) return;
    const deploymentLabel = orgName ? `the Fkh deployment for ${orgName}` : 'the Fkh deployment';
    if (!window.confirm(`Are you sure you want to stop ${deploymentLabel}? All containers will become unavailable.`)) return;
    try {
      await invokeFunction(backendUrl, token, 'StopFkh', {});
      setSystemStopped(true);
    } catch (e) {
      if (e instanceof SystemStoppedError) {
        setSystemStopped(true);
      } else if (e instanceof AuthorizationError) {
        handleAuthFailure(e);
      } else {
        setContainersError(e instanceof Error ? e.message : 'Stop Fkh Deployment failed');
      }
    }
  }, [token, backendUrl, handleAuthFailure, orgName]);

  // Loading spinner while checking stored token
  if (checking) {
    return (
      <div className="loading-screen">
        <div className="spinner" />
      </div>
    );
  }

  // Not authenticated — show login
  if (!user || !token) {
    return <Login backendUrl={backendUrl} clientId={getClientId()} message={loginMessage} onToken={handleToken} onPat={handleToken} />;
  }

  // No backend URL configured
  if (!backendUrl) {
    return (
      <div className="error-screen">
        <h2>No backend URL</h2>
        <p>
          Add <code>?backendUrl=https://your-backend.azurewebsites.net/api</code> to the URL,
          or deploy the web app to <code>fkh-&lt;org&gt;-web.azurewebsites.net</code> for automatic detection.
        </p>
        <button className="btn btn-secondary" onClick={handleSignOut}>Sign out</button>
      </div>
    );
  }

  // System is stopped — show start page
  if (systemStopped) {
    return (
      <div className="app">
        <Header user={user} orgName={orgName} backendUrl={backendUrl} isAdmin={isAdmin} onStopFkh={handleStopFkh} onSignOut={handleSignOut} />
        <main className="app-main">
          <SystemStopped
            backendUrl={backendUrl}
            token={token}
            orgName={orgName}
            isAdmin={isAdmin}
            onStarted={() => {
              setSystemStopped(false);
              fetchContainers();
            }}
          />
        </main>
      </div>
    );
  }

  return (
    <div className="app">
      <Header user={user} orgName={orgName} backendUrl={backendUrl} isAdmin={isAdmin} onStopFkh={handleStopFkh} onSignOut={handleSignOut} />
      <main className="app-main">
        <ContainerList
          containers={containers}
          loading={containersLoading}
          error={containersError}
          showAll={showAll}
          canShowAll={isAdmin}
          onToggleAll={handleToggleAll}
          onRefresh={() => fetchContainers()}
          onStart={handleStart}
          onStop={handleStop}
          actionInProgress={actionInProgress}
        />
      </main>
    </div>
  );
}
