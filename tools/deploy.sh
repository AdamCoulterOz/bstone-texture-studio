#!/usr/bin/env bash
# Publish the studio to its GitHub Pages site.
#
# This exists as a script rather than a documented sequence because one step is easy to leave
# out and fails silently: Pages serves the app from a subpath, so index.html's <base href> is
# rewritten after publish, which invalidates the integrity hash the service worker holds for
# it. The worker's install then throws, nothing ever reaches "waiting", and the app can never
# offer an update. Two deploys shipped that way before it was noticed — hence sw_manifest.py,
# and hence the verify pass that refuses to push a tree whose hashes do not match.
#
#   tools/deploy.sh "simplified checker icon (18d8d68)"
set -euo pipefail

OWNER=AdamCoulterOz
REPO=retro-texture-studio
MSG=${1:?usage: tools/deploy.sh "<what changed>"}

ROOT=$(cd "$(dirname "$0")/.." && pwd)
OUT=$(mktemp -d)
trap 'rm -rf "$OUT"' EXIT

echo "==> publishing"
dotnet publish "$ROOT/src/TextureStudio.App" -c Release -p:WasmNative=true -o "$OUT" --nologo

cd "$OUT/wwwroot"

echo "==> rewriting base href to /$REPO/"
sed -i '' "s|<base href=\"/\" />|<base href=\"/$REPO/\" />|" index.html
# A silent no-op here serves an index that cannot find its own assets, so insist it took.
grep -q "<base href=\"/$REPO/\" />" index.html \
  || { echo "base href rewrite did not match — check index.html's markup" >&2; exit 1; }

echo "==> re-stamping the service worker for the rewritten files"
python3 "$ROOT/tools/sw_manifest.py" .

touch .nojekyll
cp index.html 404.html            # Pages 404s deep links; the app owns its own routing
find . -name "*.br" -delete
find . -name "*.gz" -delete       # Pages won't content-negotiate, so these are dead weight

echo "==> verifying every asset against the manifest"
python3 "$ROOT/tools/sw_manifest.py" . --verify

echo "==> pushing gh-pages"
git init -q -b gh-pages
git add -A
git -c user.name="$(git -C "$ROOT" config user.name)" \
    -c user.email="$(git -C "$ROOT" config user.email)" \
    commit -qm "Deploy: $MSG"
git push -qf "https://github.com/$OWNER/$REPO.git" gh-pages

echo "==> waiting for the Pages build"
for _ in $(seq 1 40); do
  status=$(gh api "repos/$OWNER/$REPO/pages/builds/latest" --jq '.status' 2>/dev/null || echo)
  [ "$status" = "built" ] && break
  sleep 5
done
echo "    status: ${status:-unknown}"
echo "    https://$OWNER.github.io/$REPO/"
