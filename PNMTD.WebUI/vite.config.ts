import { defineConfig } from 'vitest/config'
import vue from '@vitejs/plugin-vue'
import { fileURLToPath, URL } from 'node:url'

// The UI lives under /pnmt-admin/ (PNMTD/Hosting/HostingSetup.cs, WebUi.BasePath). In development
// (WEB=dev ./start.sh) Vite serves it with HMR and proxies the API and the sensor API to the app;
// PNMTD_API_URL is the app's address (inside start.sh's container: its name on the network).
const api = process.env.PNMTD_API_URL ?? 'http://localhost:7327'
const proxy = { target: api, changeOrigin: false, secure: false, ws: false }

export default defineConfig({
  base: '/pnmt-admin/',
  plugins: [vue()],
  resolve: { alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) } },
  server: {
    host: true,
    port: 5173,
    strictPort: true,
    proxy: { '/api': proxy, '/event': proxy, '/read': proxy, '/healthz': proxy },
  },
  build: {
    outDir: 'dist',
    assetsDir: 'assets',
    sourcemap: false,
    // No data: URIs: the CSP allows fonts and scripts from 'self' only (PNMTD/Hosting/HostingSetup.cs).
    assetsInlineLimit: 0,
    chunkSizeWarningLimit: 1200,
  },
  test: {
    environment: 'jsdom',
    include: ['src/**/*.test.ts'],
    restoreMocks: true,
  },
})
