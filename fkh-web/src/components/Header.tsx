import type { GitHubUser } from '../types';
import { DropdownMenu } from './DropdownMenu';
import type { MenuEntry } from './DropdownMenu';

interface HeaderProps {
  user: GitHubUser;
  orgName: string;
  backendUrl: string;
  onStopFkh: () => void;
  onSignOut: () => void;
}

export function Header({ user, orgName, onStopFkh, onSignOut }: HeaderProps) {
  const menuItems: MenuEntry[] = [
    { label: 'Stop Fkh', onClick: onStopFkh, danger: true },
    { separator: true },
    { label: 'Sign out', onClick: onSignOut },
    { label: 'Exit', onClick: () => window.close() },
  ];

  return (
    <header className="app-header">
      <div className="header-left">
        <h1 className="header-title">Fkh</h1>
        {orgName && <span className="header-org">{orgName}</span>}
      </div>
      <div className="header-right">
        <img src={user.avatar_url} alt={user.login} className="header-avatar" />
        <span className="header-user">{user.name ?? user.login}</span>
        <DropdownMenu items={menuItems} triggerClass="btn btn-sm btn-secondary" trigger="☰" />
      </div>
    </header>
  );
}
