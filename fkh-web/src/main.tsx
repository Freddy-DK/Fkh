import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from './App.tsx';
import { resolveBackendUrl, getOrgNameFromUrl } from './api.ts';
import './styles.css';

const backendUrl = resolveBackendUrl();
const orgName = getOrgNameFromUrl(backendUrl);
if (orgName) {
  document.title = `Fkh - ${orgName}`;
}

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
);

if ('serviceWorker' in navigator) {
  navigator.serviceWorker.register('/sw.js');
}
