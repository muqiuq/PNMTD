import { defineConfig, type Plugin } from 'vite'
import vue from '@vitejs/plugin-vue'
import { readFileSync } from 'node:fs'
import { fileURLToPath, URL } from 'node:url'
import { gzipSync } from 'node:zlib'

// The board app (docs/boards-plan.md §4.3): its own entry, base and output, served by PNMT under /pnmt-board/ (BoardHosting.cs). It shares
// src/ with the admin UI, but the lint rule keeps the admin's client, stores, router and views out of src/boards and src/board-app.
// __BOARD_VERSION__ is the release, which the view's appVersion is compared with: a new PNMT reloads the screens.
/**
 * The board bundle's check (docs/boards-plan.md §4.3), run by every build: the build fails when a module of the admin UI (its views,
 * stores, router, client) or of its heavy libraries (PrimeVue, Monaco, Mermaid, Milkdown, highlight.js) got in, or when the JavaScript is
 * over 250 kB gzipped. The lint rule keeps the imports out; this keeps what they pull in out.
 */
function bundleGuard(): Plugin {
  const forbidden = [
    /\/src\/(views|stores|router|layouts)\//,
    /\/src\/api\/(client|resources|queries)/,
    /node_modules\/(primevue|@primeuix|monaco-editor|mermaid|@milkdown|highlight\.js|@tanstack|pinia|vue-router)\//,
  ]
  return {
    name: 'pnmt-board-bundle-guard',
    apply: 'build',
    generateBundle(_, bundle) {
      let gzipped = 0
      for (const chunk of Object.values(bundle)) {
        if (chunk.type !== 'chunk') continue
        const ids = (chunk as { moduleIds?: string[] }).moduleIds ?? Object.keys(chunk.modules ?? {})
        const wrong = ids.find((id) => forbidden.some((f) => f.test(id.replaceAll('\\', '/'))))
        if (wrong) this.error(`The board app must not bundle ${wrong} (docs/boards-plan.md §4.3).`)
        gzipped += gzipSync(chunk.code).length
      }
      if (gzipped > 250 * 1024)
        this.error(`The board app's JavaScript is ${Math.round(gzipped / 1024)} kB gzipped: over 250 kB.`)
    },
  }
}

const version = (JSON.parse(readFileSync(new URL('./package.json', import.meta.url), 'utf8')) as { version: string })
  .version
const api = process.env.PNMTD_API_URL ?? 'http://localhost:7327'

export default defineConfig(({ command }) => ({
  // board/index.html is its page: a root of its own, so that neither the build nor the dev server meets the admin's index.html.
  root: fileURLToPath(new URL('./board', import.meta.url)),
  base: '/pnmt-board/',
  plugins: [vue(), bundleGuard()],
  resolve: { alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) } },
  define: { __BOARD_VERSION__: JSON.stringify(command === 'serve' ? 'dev' : version) },
  publicDir: false,
  server: {
    host: true,
    port: 5174,
    strictPort: true,
    proxy: { '/pnmt-board/api': { target: api, changeOrigin: false, secure: false } },
  },
  build: {
    outDir: fileURLToPath(new URL('./dist-board', import.meta.url)),
    emptyOutDir: true,
    assetsDir: 'assets',
    sourcemap: false,
    manifest: true,
    // No data: URIs: the CSP allows fonts and scripts from 'self' only.
    assetsInlineLimit: 0,
  },
}))
