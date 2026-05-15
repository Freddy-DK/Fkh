import type { GitHubUser } from '../types';

interface HeaderProps {
  user: GitHubUser;
  orgName: string;
  backendUrl: string;
  onSignOut: () => void;
}

export function Header({ user, orgName, onSignOut }: HeaderProps) {
  return (
    <header className="app-header">
      <div className="header-left">
        <h1 className="header-title">Fkh</h1>
        {orgName && <span className="header-org">{orgName}</span>}
      </div>
      <div className="header-right">
        <img src={user.avatar_url} alt={user.login} className="header-avatar" />
        <span className="header-user">{user.name ?? user.login}</span>
        <button className="btn btn-sm btn-secondary" onClick={onSignOut}>
          Sign out
        </button>
      </div>
    </header>
  );
}
