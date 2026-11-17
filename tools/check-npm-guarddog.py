#!/usr/bin/env python3
"""GuardDog over every package in the web UI's lock file (docs/webui-migration-plan.md §7.1).

  python3 tools/check-npm-guarddog.py [--batch N/M] [--lock PNMTD.WebUI/package-lock.json]
                                      [--accepted tools/guarddog-accepted.json] [--guarddog guarddog]

GuardDog (DataDog's static rules for malicious npm packages) scans the dependencies of a package.json,
not a lock file: the lock file's packages are written out as package.json files of exact versions (a
name once per file, so two versions of one package go to two files) and scanned. Then:
  - capability-* results only describe what a package can do (network, processes, files) and are not
    findings;
  - every other finding (threat-*, typosquatting, metadata_mismatch, deceptive_author, bundled_binary,
    ...) must be in the accepted list: its rule, the package (a name, or a pattern like
    @rolldown/binding-*), its exact version or any version ("*"), and the reason;
  - a rule GuardDog could not evaluate for a package is a warning, not a failure.
Exits 1 naming each new finding, with the line that accepts it. --batch 2/4 scans the second quarter
of the packages: the supply-chain workflow runs the four quarters in parallel, each against the whole
accepted list. tools/guarddog.sh runs this in a container.
"""
import argparse
import fnmatch
import json
import os
import subprocess
import sys
import tempfile

NODE_MODULES = "node_modules/"


def lock_packages(path):
    """Every (name, version) the lock file installs, nested copies included, sorted."""
    with open(path, encoding="utf-8") as f:
        lock = json.load(f)
    pairs = set()
    for key, entry in lock.get("packages", {}).items():
        if key == "" or entry.get("link"):
            continue
        name = entry.get("name") or key[key.rfind(NODE_MODULES) + len(NODE_MODULES):]
        pairs.add((name, entry["version"]))
    return sorted(pairs)


def batch(pairs, spec):
    """Every M-th package from the N-th, for --batch N/M; all of them without."""
    if not spec:
        return pairs
    try:
        n, m = (int(x) for x in spec.split("/"))
    except ValueError:
        n, m = 0, 0
    if not 1 <= n <= m:
        sys.exit(f"--batch {spec}: expected N/M with 1 <= N <= M")
    return [p for i, p in enumerate(pairs) if i % m == n - 1]


def package_jsons(pairs):
    """The dependencies of as few package.json files as hold every pair, a name once per file."""
    files = []
    for name, version in pairs:
        for deps in files:
            if name not in deps:
                deps[name] = version
                break
        else:
            files.append({name: version})
    return files


def run_guarddog(exe, deps, folder, index):
    path = os.path.join(folder, f"package-{index}.json")
    with open(path, "w", encoding="utf-8") as f:
        json.dump({"name": f"pnmtd-lock-{index}", "version": "0.0.0", "private": True, "dependencies": deps}, f)
    run = subprocess.run([exe, "npm", "verify", path, "--output-format", "json"], capture_output=True, text=True)
    try:
        return json.loads(run.stdout)
    except json.JSONDecodeError:
        sys.exit(f"guarddog gave no JSON for {path} (exit {run.returncode}):\n{run.stderr[-2000:]}\n{run.stdout[-500:]}")


def detail(value):
    """One line of what GuardDog saw: the first location and its code, or the message."""
    if isinstance(value, list) and value:
        first = value[0]
        if isinstance(first, dict):
            code = " ".join(str(first.get("code") or first.get("message") or "").split())
            return f"{first.get('location', '')}  {code[:160]}".strip()
        return " ".join(str(first).split())[:200]
    return " ".join(str(value).split())[:200]


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--batch", help="N/M: the N-th of M interleaved parts of the packages")
    ap.add_argument("--lock", default="PNMTD.WebUI/package-lock.json")
    ap.add_argument("--accepted", default="tools/guarddog-accepted.json")
    ap.add_argument("--guarddog", default="guarddog", help="the guarddog executable")
    args = ap.parse_args()

    with open(args.accepted, encoding="utf-8") as f:
        accepted = json.load(f)["accepted"]
    pairs = batch(lock_packages(args.lock), args.batch)
    files = package_jsons(pairs)

    findings, warnings, scanned = [], [], 0
    with tempfile.TemporaryDirectory() as folder:
        for index, deps in enumerate(files, 1):
            for r in run_guarddog(args.guarddog, deps, folder, index):
                scanned += 1
                name, version = r.get("dependency"), r.get("version")
                result = r.get("result") or {}
                for rule, error in (result.get("errors") or {}).items():
                    warnings.append(f"{name}@{version}: {rule} could not be evaluated: {' '.join(str(error).split())[:160]}")
                for rule, value in (result.get("results") or {}).items():
                    if rule.startswith("capability-") or value in (None, [], {}, ""):
                        continue
                    findings.append((rule, name, version, detail(value)))
    if scanned != len(pairs):
        warnings.append(f"{len(pairs)} packages asked for, {scanned} scanned")

    def matches(a, rule, name, version=None):
        return (a["rule"] == rule and fnmatch.fnmatchcase(name, a["package"])
                and (version is None or a.get("version", "*") in ("*", version)))

    def accepts(rule, name, version):
        return any(matches(a, rule, name, version) for a in accepted)

    new = [f for f in findings if not accepts(*f[:3])]
    print(f"{scanned} packages in {len(files)} package.json file(s): {len(findings)} finding(s), {len(findings) - len(new)} accepted, {len(warnings)} warning(s).")
    for w in warnings:
        print(f"  warning: {w}")
    if not args.batch:
        # The whole lock file: an accepted entry nothing matched any more can go.
        for a in accepted:
            if not any(matches(a, f[0], f[1]) for f in findings):
                print(f"  stale: {a['rule']} {a['package']}@{a.get('version', '*')} matched nothing")
    for rule, name, version, seen in new:
        line = json.dumps({"rule": rule, "package": name, "version": version, "reason": ""})
        print(f"NEW  {rule}  {name}@{version}\n     {seen}\n     to accept: {line}")
    if new:
        print(f"{len(new)} new finding(s): look at each, then accept it in {args.accepted} with its reason.")
        sys.exit(1)


if __name__ == "__main__":
    main()
