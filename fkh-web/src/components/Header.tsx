import type { GitHubUser } from '../types';
import { DropdownMenu } from './DropdownMenu';
import type { MenuEntry } from './DropdownMenu';

interface HeaderProps {
  user: GitHubUser;
  orgName: string;
  backendUrl: string;
  isAdmin: boolean;
  onStopFkh: () => void;
  onSignOut: () => void;
}

export function Header({ user, orgName, isAdmin, onStopFkh, onSignOut }: HeaderProps) {
  const userDisplayName = user.name ? `${user.name} (@${user.login})` : `@${user.login}`;
  const menuItems: MenuEntry[] = [
    { label: 'Stop Fkh Deployment', onClick: onStopFkh, danger: true, disabled: !isAdmin },
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
        <span className="header-user" title={userDisplayName}>{userDisplayName}</span>
        <DropdownMenu items={menuItems} triggerClass="btn btn-sm btn-secondary" trigger="☰" />
      </div>
    </header>
  );
}
