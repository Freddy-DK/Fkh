import { useState, useCallback } from 'react';
import { startDeviceFlow, pollDeviceFlow, type DeviceFlowCodes } from '../auth';

interface LoginProps {
  backendUrl: string;
  clientId: string;
  message: string | null;
  onToken: (token: string) => void;
  onPat: (token: string) => void;
}

export function Login({ backendUrl, clientId, message, onToken, onPat }: LoginProps) {
  const [mode, setMode] = useState<'choose' | 'device' | 'pat'>('choose');
  const [deviceCodes, setDeviceCodes] = useState<DeviceFlowCodes | null>(null);
  const [polling, setPolling] = useState(false);
  const [error, setError] = useState('');
  const [pat, setPat] = useState('');

  const startDevice = useCallback(async () => {
    if (!clientId) {
      setError('No GitHub OAuth Client ID configured. Use a Personal Access Token instead, or add ?clientId=... to the URL.');
      setMode('pat');
      return;
    }
    if (!backendUrl) {
      setError('No backend URL configured. Add ?backendUrl=... to the URL.');
      setMode('pat');
      return;
    }
    try {
      setError('');
      const codes = await startDeviceFlow(backendUrl, clientId);
      setDeviceCodes(codes);
      setMode('device');
      setPolling(true);

      // Poll for completion
      const interval = (codes.interval ?? 5) * 1000;
      const deadline = Date.now() + codes.expires_in * 1000;
      const poll = async () => {
        while (Date.now() < deadline) {
          await new Promise(r => setTimeout(r, interval));
          try {
            const result = await pollDeviceFlow(backendUrl, clientId, codes.device_code);
            if (result.access_token) {
              setPolling(false);
              onToken(result.access_token);
              return;
            }
            if (result.error === 'slow_down') {
              await new Promise(r => setTimeout(r, 5000));
            } else if (result.error && result.error !== 'authorization_pending') {
              setError(result.error_description ?? result.error);
              setPolling(false);
              return;
            }
          } catch (e) {
            setError(e instanceof Error ? e.message : 'Polling failed');
            setPolling(false);
            return;
          }
        }
        setError('Device code expired. Please try again.');
        setPolling(false);
      };
      poll();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to start device flow');
    }
  }, [backendUrl, clientId, onToken]);

  const submitPat = () => {
    const trimmed = pat.trim();
    if (!trimmed) return;
    onPat(trimmed);
  };

  return (
    <div className="login-container">
      <div className="login-card">
        <h1>Fkh</h1>
        <p className="login-subtitle">Sign in to manage your Business Central containers</p>

        {(error || message) && <div className="error-banner">{error || message}</div>}

        {mode === 'choose' && (
          <div className="login-options">
            <button className="btn btn-primary" onClick={startDevice}>
              Sign in with GitHub
            </button>
            <button className="btn btn-secondary" onClick={() => setMode('pat')}>
              Use Personal Access Token
            </button>
          </div>
        )}

        {mode === 'device' && deviceCodes && (
          <div className="device-flow">
            <p>Go to:</p>
            <a
              href={deviceCodes.verification_uri}
              target="_blank"
              rel="noopener noreferrer"
              className="device-link"
            >
              {deviceCodes.verification_uri}
            </a>
            <p>And enter the code:</p>
            <div className="device-code">{deviceCodes.user_code}</div>
            {polling && <p className="polling-text">Waiting for authorization...</p>}
            <button className="btn btn-secondary" onClick={() => { setMode('choose'); setPolling(false); }}>
              Cancel
            </button>
          </div>
        )}

        {mode === 'pat' && (
          <div className="pat-flow">
            <p>Enter a GitHub Personal Access Token with <code>read:user</code> and <code>read:org</code> scopes:</p>
            <input
              type="password"
              className="pat-input"
              value={pat}
              onChange={e => setPat(e.target.value)}
              onKeyDown={e => { if (e.key === 'Enter') submitPat(); }}
              placeholder="ghp_..."
              autoFocus
            />
            <div className="pat-actions">
              <button className="btn btn-primary" onClick={submitPat} disabled={!pat.trim()}>
                Sign in
              </button>
              <button className="btn btn-secondary" onClick={() => setMode('choose')}>
                Back
              </button>
            </div>
          </div>
        )}
      </div>
    </div>
  );
}
