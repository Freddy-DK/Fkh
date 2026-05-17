import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from './App.tsx';
import { resolveBackendUrl, getOrgNameFromUrl } from './api.ts';
import './styles.css';

/** Build a dynamic manifest so the PWA install name includes the org name. */
function applyDynamicManifest(orgName: string) {
  const shortName = orgName ? `Fkh - ${orgName}` : 'Fkh';
  const fullName = orgName
    ? `Fkh — ${orgName} — Business Central Containers`
    : 'Fkh — Business Central Containers';

  document.title = shortName;

  const manifest = {
    name: fullName,
    short_name: shortName,
    description: 'Manage Business Central containers on Azure Kubernetes Service',
    id: '/',
    start_url: window.location.href,
    scope: '/',
    display: 'standalone',
    orientation: 'any',
    background_color: '#0d1117',
    theme_color: '#0d1117',
    categories: ['developer', 'productivity'],
    icons: [
      { src: '/icon-192.png', sizes: '192x192', type: 'image/png' },
      { src: '/icon-512.png', sizes: '512x512', type: 'image/png' },
      { src: '/icon-512.png', sizes: '512x512', type: 'image/png', purpose: 'maskable' },
    ],
  };

  const blob = new Blob([JSON.stringify(manifest)], { type: 'application/json' });
  const url = URL.createObjectURL(blob);

  const existing = document.querySelector('link[rel="manifest"]');
  if (existing) {
    existing.setAttribute('href', url);
  } else {
    const link = document.createElement('link');
    link.rel = 'manifest';
    link.href = url;
    document.head.appendChild(link);
  }
}

const backendUrl = resolveBackendUrl();
const orgName = getOrgNameFromUrl(backendUrl);
applyDynamicManifest(orgName);

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
);

if ('serviceWorker' in navigator) {
  navigator.serviceWorker.register('/sw.js');
}
