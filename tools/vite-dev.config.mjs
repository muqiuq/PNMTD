// PNMTD.WebUI/vite.config.ts as it is, with polling for the file watcher: the config that
// `WEB=dev ./start.sh` runs Vite with in its container.
//
// On macOS, Podman's VM (virtiofs) passes no inotify events through a bind mount, so Vite would
// never see an edit and HMR would stay silent. Polling sees them.
//
// start.sh copies this file into PNMTD.WebUI/node_modules/.pnmtd-dev/ (the node_modules volume),
// so that the config's own imports resolve from node_modules, and Vite's bundled copy of it lands
// somewhere writable.
import base from '../../vite.config.ts'

function withPolling(config) {
  const server = config.server ?? {}
  return { ...config, server: { ...server, watch: { ...(server.watch ?? {}), usePolling: true, interval: 300 } } }
}

export default typeof base === 'function' ? async (env) => withPolling(await base(env)) : withPolling(base)
