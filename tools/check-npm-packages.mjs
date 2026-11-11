// The web UI's dependency check (docs/webui-migration-plan.md §5.1): run by run-tests.sh in a node
// container, no dependencies of its own.
//
//   node tools/check-npm-packages.mjs [PNMTD.WebUI]
//
// Holds PNMTD.WebUI/package.json and package-lock.json to these rules:
//   - every direct dependency is pinned to an exact version, and the lock file agrees;
//   - every package comes from registry.npmjs.org with a sha512 integrity;
//   - every package carries an open-source licence from the list below. PrimeVue 5 moved to a
//     commercial licence with a licence key; this is what stops such a package from slipping in
//     with an update;
//   - a package with an install script is listed below (npm ci runs with --ignore-scripts anyway).
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
  'ISC',
  'MIT',
  'MIT-0',
  'MPL-2.0', // lightningcss, in the build tools only
  'OFL-1.1', // the fonts (Inter, JetBrains Mono)
  'Python-2.0', // argparse
  'Unlicense',
])

// Their scripts never run (npm ci --ignore-scripts); listed so that a new one is a decision.
const allowedInstallScripts = new Set([
  'vue-demi', // picks Vue 2 or 3 files; the shipped ones are Vue 3's already
  'fsevents', // macOS only, optional, never installed in the Linux containers
])

const root = resolve(process.argv[2] ?? 'PNMTD.WebUI')
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

let count = 0
for (const [path, entry] of Object.entries(lock.packages ?? {})) {
  if (path === '' || entry.link) {
    continue
  }
  count++
  const name = path.slice(path.lastIndexOf('node_modules/') + 'node_modules/'.length)
  if (!entry.resolved?.startsWith('https://registry.npmjs.org/')) {
    problems.push(`${name}: resolved from ${entry.resolved ?? 'nowhere'}, not registry.npmjs.org`)
  }
  if (!entry.integrity?.startsWith('sha512-')) {
    problems.push(`${name}: no sha512 integrity`)
  }
  if (!licenseOk(entry.license)) {
    problems.push(`${name}@${entry.version}: licence ${JSON.stringify(entry.license ?? null)} is not on the list`)
  }
  if (entry.hasInstallScript && !allowedInstallScripts.has(name)) {
    problems.push(`${name}@${entry.version}: has an install script and is not on the list`)
  }
}

if (problems.length) {
  console.error(problems.map((p) => `  ${p}`).join('\n'))
  console.error(`${problems.length} problem(s) in ${root}/package.json or package-lock.json.`)
  process.exit(1)
}
console.log(`${count} packages: pinned, from registry.npmjs.org, open-source licences.`)
