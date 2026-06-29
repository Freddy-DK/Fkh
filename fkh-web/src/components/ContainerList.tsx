import { useState } from 'react';
import type { ContainerInfo } from '../types';
import { DropdownMenu } from './DropdownMenu';
import type { MenuEntry } from './DropdownMenu';

interface ContainerListProps {
  containers: ContainerInfo[];
  loading: boolean;
  error: string | null;
  showAll: boolean;
  onToggleAll: () => void;
  onRefresh: () => void;
  onStart: (name: string) => void;
  onStop: (name: string) => void;
  actionInProgress: string | null;
}

export function ContainerList({
  containers,
  loading,
  error,
  showAll,
  onToggleAll,
  onRefresh,
  onStart,
  onStop,
  actionInProgress,
}: ContainerListProps) {
  return (
    <div className="container-list">
      <div className="list-toolbar">
        <h2>Containers</h2>
        <div className="toolbar-actions">
          <label className="toggle-all">
            <input type="checkbox" checked={showAll} onChange={onToggleAll} />
            Show all
          </label>
          <button className="btn btn-sm btn-secondary" onClick={onRefresh} disabled={loading}>
            {loading ? 'Loading...' : 'Refresh'}
          </button>
        </div>
      </div>

      {error && <div className="error-banner">{error}</div>}

      {!loading && containers.length === 0 && !error && (
        <div className="empty-state">No containers found.</div>
      )}

      <div className="container-cards">
        {containers.map(container => (
          <ContainerCard
            key={container.appLabel}
            container={container}
            showLabel={showAll}
            onStart={onStart}
            onStop={onStop}
            actionInProgress={actionInProgress}
          />
        ))}
      </div>
    </div>
  );
}

function ContainerCard({
  container,
  showLabel,
  onStart,
  onStop,
  actionInProgress,
}: {
  container: ContainerInfo;
  showLabel: boolean;
  onStart: (name: string) => void;
  onStop: (name: string) => void;
  actionInProgress: string | null;
}) {
  const [expanded, setExpanded] = useState(false);
  const statusLower = container.status.toLowerCase();
  const isRunning = statusLower.startsWith('running');
  const isStopped = statusLower.startsWith('stopped');
  const isStarting = statusLower.startsWith('starting') || statusLower.startsWith('pending') || statusLower.startsWith('initializing');
  const isFailed = statusLower.startsWith('failed');
  const busy = actionInProgress === container.appLabel;
  const displayName = showLabel ? container.appLabel : container.name;

  const statusClass = isRunning ? 'status-running'
    : isStopped ? 'status-stopped'
    : isStarting ? 'status-starting'
    : isFailed ? 'status-failed'
    : 'status-unknown';

  return (
    <div className={`container-card ${statusClass}`}>
      <div className="card-header" onClick={() => setExpanded(!expanded)}>
        <div className="card-title-row">
          <span className={`status-dot ${statusClass}`} />
          <ContainerName name={displayName} />
          <span className="card-status">{container.status}</span>
          <ContainerMenu
            container={container}
            isRunning={isRunning}
            isStopped={isStopped}
            busy={busy}
            onStart={onStart}
            onStop={onStop}
          />
          <span className={`expand-icon ${expanded ? 'expanded' : ''}`}>▸</span>
        </div>
      </div>

      {expanded && (
        <div className="card-details">
          <DetailRow icon="ℹ️" label="Status" value={container.statusDetail} />
          {container.reason && <DetailRow icon="⚠️" label="Reason" value={container.reason} />}
          <DetailRow icon="📦" label="Image" value={container.image} />
          {container.autoStop && <DetailRow icon="⏰" label="AutoStop" value={container.autoStop} />}
          {container.repo && <DetailRow icon="📁" label="Repo" value={container.repo} />}
          {container.project && <DetailRow icon="📂" label="Project" value={container.project} />}
          {container.webClient && isRunning && (
            <div className="detail-row">
              <span className="detail-icon">🔗</span>
              <span className="detail-label">WebClient:</span>
              <a href={container.webClient} target="_blank" rel="noopener noreferrer" className="detail-link">
                Open
              </a>
            </div>
          )}
          {container.memory && <DetailRow icon="💾" label="Memory" value={container.memory} />}
        </div>
      )}
    </div>
  );
}

function ContainerName({ name }: { name: string }) {
  const splitName = splitContainerName(name);

  return (
    <span className="card-name" title={name} aria-label={name}>
      <span className="card-name-full" aria-hidden="true">{name}</span>
      <span className="card-name-compact" aria-hidden="true">
        <span className="card-name-prefix">{splitName.prefix}</span>
        {splitName.suffix && (
          <>
            <span className="card-name-separator">-</span>
            <span className="card-name-suffix">{splitName.suffix}</span>
          </>
        )}
      </span>
    </span>
  );
}

function splitContainerName(name: string): { prefix: string; suffix: string } {
  const splitIndex = name.lastIndexOf('-');

  if (splitIndex <= 0 || splitIndex >= name.length - 1) {
    return { prefix: name, suffix: '' };
  }

  return {
    prefix: name.slice(0, splitIndex),
    suffix: name.slice(splitIndex + 1),
  };
}

function ContainerMenu({
  container,
  isRunning,
  isStopped,
  busy,
  onStart,
  onStop,
}: {
  container: ContainerInfo;
  isRunning: boolean;
  isStopped: boolean;
  busy: boolean;
  onStart: (name: string) => void;
  onStop: (name: string) => void;
}) {
  const items: MenuEntry[] = [
    { label: 'Start', onClick: () => onStart(container.appLabel), disabled: busy || isRunning },
    { label: 'Stop', onClick: () => onStop(container.appLabel), disabled: busy || isStopped },
  ];

  return <DropdownMenu items={items} triggerClass="btn btn-sm btn-secondary" />;
}

function DetailRow({ icon, label, value }: { icon: string; label: string; value: string }) {
  return (
    <div className="detail-row">
      <span className="detail-icon">{icon}</span>
      <span className="detail-label">{label}:</span>
      <span className="detail-value">{value}</span>
    </div>
  );
}
