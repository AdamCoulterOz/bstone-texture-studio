#!/usr/bin/env python3
"""Re-stamp service-worker-assets.js against the files actually being deployed.

The published worker caches every asset with the integrity hash the build recorded:

    new Request(asset.url, { integrity: asset.hash, cache: "no-cache" })

cache.addAll is all-or-nothing, so one asset whose bytes no longer match its recorded hash
makes install throw. The worker then never reaches "installed", so it never waits, so the app
can never offer it as an update — and since install is also where the app gets cached, offline
stops working. None of that surfaces anywhere: the page keeps working from the network and the
only symptom is an update prompt that never appears.

Deploying to a project Pages site is exactly that situation, because index.html's <base href>
has to be rewritten after publish. So: recompute the hash of anything that changed, and
re-derive the worker's version stamp from the final content. The stamp matters because the
browser decides whether a worker is new by comparing the script's bytes, and that script's
only per-build content is the version comment the build injects — two deploys of the same
publish with different post-publish edits would otherwise look identical and never be picked
up.

    python3 tools/sw_manifest.py <dir>            # re-stamp, reporting what changed
    python3 tools/sw_manifest.py <dir> --verify   # check only; non-zero exit on any mismatch
"""
import base64
import hashlib
import json
import re
import sys
from pathlib import Path

ASSETS = "service-worker-assets.js"
WORKER = "service-worker.js"
WRAPPER = "self.assetsManifest = "
VERSION_COMMENT = re.compile(rb"^/\* Manifest version: [^*]* \*/")


def integrity(path):
    return "sha256-" + base64.b64encode(hashlib.sha256(path.read_bytes()).digest()).decode()


def load(root):
    text = (root / ASSETS).read_text()
    if not text.startswith(WRAPPER):
        sys.exit(f"{ASSETS}: expected it to start with {WRAPPER!r}")
    return json.loads(text[len(WRAPPER):].rstrip().rstrip(";"))


def stamp(manifest):
    """A version derived from the content, so any change to what is served is a new worker."""
    payload = "".join(f"{a['url']}:{a['hash']}" for a in sorted(manifest["assets"],
                                                                key=lambda a: a["url"]))
    return base64.b64encode(hashlib.sha256(payload.encode()).digest()).decode()[:8]


def main():
    args = sys.argv[1:]
    verify = "--verify" in args
    paths = [a for a in args if not a.startswith("--")]
    root = Path(paths[0] if paths else ".")

    manifest = load(root)
    missing, changed = [], []
    for asset in manifest["assets"]:
        path = root / asset["url"]
        if not path.is_file():
            missing.append(asset["url"])
            continue
        actual = integrity(path)
        if actual != asset["hash"]:
            changed.append(asset["url"])
            asset["hash"] = actual

    for url in missing:
        print(f"  MISSING  {url}")
    for url in changed:
        print(f"  {'MISMATCH' if verify else 're-stamped'}  {url}")

    if verify:
        if missing or changed:
            sys.exit(f"\n{len(missing)} missing, {len(changed)} mismatched — the worker's "
                     f"install would fail. Re-stamp before deploying.")
        print(f"  all {len(manifest['assets'])} assets match their integrity hashes")
        return

    if missing:
        sys.exit(f"\n{len(missing)} asset(s) listed in {ASSETS} are not on disk")

    version = stamp(manifest)
    manifest["version"] = version
    (root / ASSETS).write_text(WRAPPER + json.dumps(manifest, indent=2) + ";\n")

    # The build injects this comment; rewrite it so the script's bytes track the final content.
    worker = root / WORKER
    body = worker.read_bytes()
    line = f"/* Manifest version: {version} */".encode()
    worker.write_bytes(VERSION_COMMENT.sub(line, body) if VERSION_COMMENT.match(body)
                       else line + b"\n" + body)

    print(f"  {len(changed)} re-stamped, version {version}, {len(manifest['assets'])} assets")


if __name__ == "__main__":
    main()
