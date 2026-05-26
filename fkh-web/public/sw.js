// Minimal service worker for PWA installability.
// Network-first: always fetch from the network, fall back to cache for offline support.

const CACHE_NAME = 'fkh-v1';

/** Extract the org name from the referrer or client URL (e.g. ?backendUrl=...fkh-myorg-backend...) */
function getOrgName(referrerUrl) {
  try {
    const url = new URL(referrerUrl);
    // Try backendUrl query param first
    const backendUrl = url.searchParams.get('backendUrl') || '';
    const match = backendUrl.match(/fkh-(.+?)-backend/);
    if (match) return match[1];
    // Fall back to the hostname (e.g. fkh-myorg-web.azurewebsites.net)
    const hostMatch = url.hostname.match(/fkh-(.+?)-web/);
    if (hostMatch) return hostMatch[1];
  } catch {}
  return '';
}

function buildManifest(orgName, startUrl) {
  const shortName = orgName ? `Fkh - ${orgName}` : 'Fkh';
  const fullName = orgName
    ? `Fkh \u2014 ${orgName} \u2014 Business Central Containers`
    : 'Fkh \u2014 Business Central Containers';
  return {
    name: fullName,
    short_name: shortName,
    description: 'Manage Business Central containers on Azure Kubernetes Service',
    id: '/',
    start_url: startUrl || '/',
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
}

self.addEventListener('install', () => {
  self.skipWaiting();
});

self.addEventListener('activate', (event) => {
  event.waitUntil(
    caches.keys().then((names) =>
      Promise.all(names.filter((n) => n !== CACHE_NAME).map((n) => caches.delete(n)))
    )
  );
  self.clients.claim();
});

self.addEventListener('fetch', (event) => {
  // Only handle same-origin GET requests
  if (event.request.method !== 'GET') return;
  const url = new URL(event.request.url);
  if (url.origin !== self.location.origin) return;

  // Intercept manifest.json to return a dynamic manifest with the org name
  if (url.pathname === '/manifest.json') {
    event.respondWith(
      (async () => {
        // Get the referring client's URL to extract the org name
        const clientList = await self.clients.matchAll({ type: 'window' });
        const clientUrl = clientList.length > 0 ? clientList[0].url : '';
        const orgName = getOrgName(clientUrl);
        const manifest = buildManifest(orgName, clientUrl || '/');
        return new Response(JSON.stringify(manifest), {
          headers: { 'Content-Type': 'application/manifest+json' },
        });
      })()
    );
    return;
  }

  event.respondWith(
    fetch(event.request)
      .then((response) => {
        const clone = response.clone();
        caches.open(CACHE_NAME).then((cache) => cache.put(event.request, clone));
        return response;
      })
      .catch(() => caches.match(event.request))
  );
});
