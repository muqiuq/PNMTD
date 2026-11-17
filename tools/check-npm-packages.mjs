// The web UI's dependency check (docs/webui-migration-plan.md §5.1, §7.1): run by run-tests.sh in a node
// container, no dependencies of its own.
//
//   node tools/check-npm-packages.mjs [PNMTD.WebUI] [--offline]
//
// Holds PNMTD.WebUI/package.json and package-lock.json to these rules:
//   - every direct dependency is pinned to an exact version, and the lock file agrees;
//   - every package comes from registry.npmjs.org with a sha512 integrity;
//   - every package carries an open-source licence from the list below. PrimeVue 5 moved to a
//     commercial licence with a licence key; this is what stops such a package from slipping in
//     with an update;
//   - a package with an install script is listed below (npm ci runs with --ignore-scripts anyway);
//   - asked of the registry (not with --offline): every version was published at least
//     minimumAgeDays ago, is still there, and has the lock file's integrity there. The compromised
//     versions of 2025 (chalk and debug, the Shai-Hulud worm) were pulled within hours or days, so
//     a version that young waits, or is listed below with the reason. A version the registry no
//     longer has was pulled for a reason, and an integrity the registry doesn't know is not the
//     registry's file.
// Exits 1 naming each offender.
import { readFileSync } from 'node:fs'
import { join, resolve } from 'node:path'

const allowedLicenses = new Set([
  '0BSD',
  'Apache-2.0',
  'BlueOak-1.0.0',
  'BSD-2-Clause',
  'BSD-3-Clause',
  'CC0-1.0',
  'CC-BY-4.0',
  'EPL-2.0', // elkjs, Mermaid's ELK layout (docs/locations-knowledge-plan.md §5.8): weak copyleft per file, used unmodified
  'ISC',
  'MIT',
  'MIT-0',
  'MPL-2.0', // lightningcss, in the build tools only
  'OFL-1.1', // the fonts (Inter, JetBrains Mono)
  'Python-2.0', // argparse
  'Unlicense',
])

// Packages whose package.json names no licence, read from their licence file by hand, per version:
// another version is checked again.
const licensesReadByHand = new Map([
  ['khroma@2.1.0', 'MIT'], // Mermaid's colours; its "license" file
])

// Their scripts never run (npm ci --ignore-scripts); listed so that a new one is a decision.
const allowedInstallScripts = new Set([
  'vue-demi', // picks Vue 2 or 3 files; the shipped ones are Vue 3's already
  'fsevents', // macOS only, optional, never installed in the Linux containers
])

// A version younger than this, by the registry's publish time, waits. Malicious versions are found
// and pulled within this time; a version still there after it has been seen by many.
const minimumAgeDays = 7

// Versions used before they were a week old, each with the reason it couldn't wait (a fix the UI
// needs now, read by hand); the rule applies again to the next version.
const inLockBeforeTheRule = 'in the lock file when the rule came (2026-11-17); signatures and GuardDog checked that day' // all older than a week from 2026-11-20: remove then
const youngAllowed = new Map([
  // ['name@1.2.3', 'why'],
  ['@codemirror/lint@6.9.8', inLockBeforeTheRule],
  ['@rolldown/binding-android-arm-eabi@1.2.19', inLockBeforeTheRule],
  ['@rolldown/binding-android-arm64@1.2.19', inLockBeforeTheRule],
  ['@rolldown/binding-darwin-arm64@1.2.19', inLockBeforeTheRule],
  ['@rolldown/binding-darwin-x64@1.2.19', inLockBeforeTheRule],
  ['@rolldown/binding-freebsd-x64@1.2.19', inLockBeforeTheRule],
  ['@rolldown/binding-linux-arm-gnueabihf@1.2.19', inLockBeforeTheRule],
  ['@rolldown/binding-linux-arm64-gnu@1.2.19', inLockBeforeTheRule],
  ['@rolldown/binding-linux-arm64-musl@1.2.19', inLockBeforeTheRule],
  ['@rolldown/binding-linux-ppc64-gnu@1.2.19', inLockBeforeTheRule],
  ['@rolldown/binding-linux-s390x-gnu@1.2.19', inLockBeforeTheRule],
  ['@rolldown/binding-linux-x64-gnu@1.2.19', inLockBeforeTheRule],
  ['@rolldown/binding-linux-x64-musl@1.2.19', inLockBeforeTheRule],
  ['@rolldown/binding-openharmony-arm64@1.2.19', inLockBeforeTheRule],
  ['@rolldown/binding-wasm32-wasi@1.2.19', inLockBeforeTheRule],
  ['@rolldown/binding-win32-arm64-msvc@1.2.19', inLockBeforeTheRule],
  ['@rolldown/binding-win32-x64-msvc@1.2.19', inLockBeforeTheRule],
  ['brace-expansion@2.1.8', inLockBeforeTheRule],
  ['brace-expansion@5.0.13', inLockBeforeTheRule],
  ['cytoscape@3.35.0', inLockBeforeTheRule],
  ['mdast-util-to-hast@13.2.2', inLockBeforeTheRule],
  ['prettier@3.10.0', inLockBeforeTheRule],
  ['prosemirror-inputrules@1.6.0', inLockBeforeTheRule],
  ['rolldown@1.2.19', inLockBeforeTheRule],
  ['tldts-core@7.4.22', inLockBeforeTheRule],
  ['tldts@7.4.22', inLockBeforeTheRule],
  ['vite@8.3.8', inLockBeforeTheRule],
  ['vue-router@5.5.0', inLockBeforeTheRule],
])

const args = process.argv.slice(2)
const offline = args.includes('--offline')
const root = resolve(args.find((a) => !a.startsWith('--')) ?? 'PNMTD.WebUI')
const pkg = JSON.parse(readFileSync(join(root, 'package.json'), 'utf8'))
const lock = JSON.parse(readFileSync(join(root, 'package-lock.json'), 'utf8'))
const problems = []

const exact = /^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$/
const lockRoot = lock.packages?.[''] ?? {}
for (const section of ['dependencies', 'devDependencies']) {
  for (const [name, version] of Object.entries(pkg[section] ?? {})) {
    if (!exact.test(version)) {
      problems.push(`${name}: "${version}" is not an exact version (package.json ${section})`)
    }
    if (lockRoot[section]?.[name] !== version) {
      problems.push(`${name}: package-lock.json has ${lockRoot[section]?.[name] ?? 'nothing'}, package.json ${version}`)
    }
    const installed = lock.packages?.[`node_modules/${name}`]?.version
    if (installed !== version) {
      problems.push(`${name}: package-lock.json installs ${installed ?? 'nothing'}, package.json wants ${version}`)
    }
  }
}

if ((lock.lockfileVersion ?? 0) < 3) {
  problems.push(`package-lock.json is lockfileVersion ${lock.lockfileVersion}; npm 9 or later writes 3`)
}

function licenseOk(license) {
  if (typeof license !== 'string') {
    return false
  }
  // "(MIT OR CC0-1.0)": one allowed choice is enough. "AND" needs all of them.
  const expression = license.replace(/[()]/g, '').trim()
  if (expression.includes(' AND ')) {
    return expression.split(' AND ').every((l) => allowedLicenses.has(l.trim()))
  }
  return expression.split(' OR ').some((l) => allowedLicenses.has(l.trim()))
}

// Every installed package by name: the versions the lock holds of it (nested copies included).
const installed = new Map()
let count = 0
for (const [path, entry] of Object.entries(lock.packages ?? {})) {
  if (path === '' || entry.link) {
    continue
  }
  count++
  const name = entry.name ?? path.slice(path.lastIndexOf('node_modules/') + 'node_modules/'.length)
  if (!entry.resolved?.startsWith('https://registry.npmjs.org/')) {
    problems.push(`${name}: resolved from ${entry.resolved ?? 'nowhere'}, not registry.npmjs.org`)
  }
  if (!entry.integrity?.startsWith('sha512-')) {
    problems.push(`${name}: no sha512 integrity`)
  }
  if (!licenseOk(entry.license ?? licensesReadByHand.get(`${name}@${entry.version}`))) {
    problems.push(`${name}@${entry.version}: licence ${JSON.stringify(entry.license ?? null)} is not on the list`)
  }
  if (entry.hasInstallScript && !allowedInstallScripts.has(name)) {
    problems.push(`${name}@${entry.version}: has an install script and is not on the list`)
  }
  if (!installed.has(name)) {
    installed.set(name, new Map())
  }
  installed.get(name).set(entry.version, entry.integrity)
}

// The registry's record of a package (its versions, their publish times and integrities), or the
// reason it couldn't be read. One retry: a run shouldn't fail on one dropped connection.
async function packument(name) {
  const url = `https://registry.npmjs.org/${name}`
  let last
  for (let attempt = 1; attempt <= 2; attempt++) {
    try {
      const response = await fetch(url, { headers: { accept: 'application/json' }, signal: AbortSignal.timeout(90_000) })
      if (response.ok) {
        return { doc: await response.json() }
      }
      if (response.status === 404) {
        return { error: 'the registry has no package of this name' }
      }
      last = `the registry answered ${response.status}`
    } catch (e) {
      last = e?.cause?.message ?? e?.message ?? String(e)
    }
  }
  return { error: last }
}

async function checkRegistry() {
  const names = [...installed.keys()]
  const now = Date.now()
  let next = 0
  async function worker() {
    while (next < names.length) {
      const name = names[next++]
      const { doc, error } = await packument(name)
      if (!doc) {
        problems.push(`${name}: the registry's record could not be read: ${error}`)
        continue
      }
      for (const [version, integrity] of installed.get(name)) {
        const known = doc.versions?.[version]
        if (!known) {
          problems.push(`${name}@${version}: the registry no longer has this version (unpublished or removed)`)
          continue
        }
        if (known.dist?.integrity && known.dist.integrity !== integrity) {
          problems.push(`${name}@${version}: the lock file's integrity is not the registry's (${known.dist.integrity.slice(0, 24)}…)`)
        }
        const published = doc.time?.[version]
        if (!published) {
          problems.push(`${name}@${version}: the registry records no publish time for this version`)
          continue
        }
        const ageDays = (now - Date.parse(published)) / 86_400_000
        if (ageDays < minimumAgeDays && !youngAllowed.has(`${name}@${version}`)) {
          problems.push(
            `${name}@${version}: published ${ageDays.toFixed(1)} days ago (${published.slice(0, 10)}), younger than ${minimumAgeDays} days: wait, or list it with the reason`,
          )
        }
      }
    }
  }
  await Promise.all(Array.from({ length: 12 }, worker))
}

if (!offline) {
  await checkRegistry()
}

if (problems.length) {
  console.error(problems.sort().map((p) => `  ${p}`).join('\n'))
  console.error(`${problems.length} problem(s) in ${root}/package.json or package-lock.json.`)
  process.exit(1)
}
console.log(
  `${count} packages: pinned, from registry.npmjs.org, open-source licences` +
    (offline ? ' (the registry not asked: --offline).' : `, on the registry with their integrity and ${minimumAgeDays} or more days old.`),
)
