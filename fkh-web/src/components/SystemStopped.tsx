import { useState } from 'react';
import { startFkh } from '../api';

interface SystemStoppedProps {
  backendUrl: string;
  token: string;
  orgName: string;
  isAdmin: boolean;
  onStarted: () => void;
}

export function SystemStopped({ backendUrl, token, orgName, isAdmin, onStarted }: SystemStoppedProps) {
  const [starting, setStarting] = useState(false);
  const [status, setStatus] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const deploymentLabel = orgName ? `Fkh deployment for ${orgName}` : 'Fkh deployment';

  const handleStart = async () => {
    setStarting(true);
    setError(null);
    setStatus('Starting the system...');
    try {
      await startFkh(backendUrl, token, (msg) => setStatus(msg));
      setStatus('System is starting — please wait while services come online...');
      // Wait 5 minutes for the system to fully come online before redirecting
      setTimeout(onStarted, 300_000);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to start the Fkh deployment');
      setStarting(false);
      setStatus(null);
    }
  };

  return (
    <div className="stopped-container">
      <div className="stopped-card">
        <div className="stopped-icon">&#x23F8;</div>
        <h1>System Stopped</h1>
        <p className="stopped-description">
          The {deploymentLabel} is currently stopped. Start the {deploymentLabel} to manage your containers.
        </p>
        {error && <div className="error-banner">{error}</div>}
        {status && <p className="stopped-status">{status}</p>}
        <button
          className="btn btn-primary btn-lg"
          onClick={handleStart}
          disabled={starting || !isAdmin}
          title={!isAdmin ? 'Administrators only' : undefined}
        >
          {starting ? 'Starting...' : 'Start Fkh Deployment'}
        </button>
      </div>
    </div>
  );
}
