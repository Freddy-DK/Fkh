import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import { writeFileSync } from 'fs';
import { resolve } from 'path';

function getOrgNameFromUrl(url: string): string {
  const match = url.match(/fkh-(.+?)-backend/);
  return match ? match[1] ?? '' : '';
}

/** Vite plugin that stamps manifest.json with the org name from VITE_BACKEND_URL at build time. */
function manifestPlugin() {
  return {
    name: 'fkh-manifest',
    writeBundle(options: { dir?: string }) {
      const backendUrl = process.env.VITE_BACKEND_URL ?? '';
      const orgName = getOrgNameFromUrl(backendUrl);
      const shortName = orgName ? `Fkh - ${orgName}` : 'Fkh';
      const fullName = orgName
        ? `Fkh \u2014 ${orgName} \u2014 Business Central Containers`
        : 'Fkh \u2014 Business Central Containers';

      const manifest = {
        name: fullName,
        short_name: shortName,
        description: 'Manage Business Central containers on Azure Kubernetes Service',
        id: '/',
        start_url: '/',
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

      const outDir = options.dir ?? 'dist';
      writeFileSync(resolve(outDir, 'manifest.json'), JSON.stringify(manifest, null, 2));
    },
  };
}

export default defineConfig({
  plugins: [react(), manifestPlugin()],
  build: {
    outDir: 'dist',
  },
});
