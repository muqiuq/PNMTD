// PNMTD.WebUI/vite.board.config.ts as it is, with polling for the file watcher: the board app's config (docs/boards-plan.md §4.3) that
// `WEB=dev ./start.sh` runs a second Vite with, beside the admin UI's (see vite-dev.config.mjs for why polling, and where start.sh
// copies this file).
import base from '../../vite.board.config.ts'

function withPolling(config) {
  const server = config.server ?? {}
  return { ...config, server: { ...server, watch: { ...(server.watch ?? {}), usePolling: true, interval: 300 } } }
}

export default typeof base === 'function' ? async (env) => withPolling(await base(env)) : withPolling(base)
