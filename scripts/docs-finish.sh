#!/usr/bin/env bash
# What every docs build (scripts/docs.sh and .github/workflows/docs.yml) does after docs-builder: the landing page over
# the generated index.html, the files docs-builder doesn't copy, and the snapshots theme appended to the design
# system's stylesheet. Needs a build with FEATURE_NAVIGATION_PREVIEW=true: without it the pages load no ds.css and
# this fails. Adapted from nullean/curb's.
#
#   scripts/docs-finish.sh .artifacts/docs/html [path prefix]
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
out="${1:?usage: docs-finish.sh <docs-builder output dir> [path prefix]}"
prefix="${2:-}"

# The theme goes into the ds.css the pages actually load. Its name comes from docs-builder (plain ds.css with ?v=, or a
# hashed file), so read it from a built page instead of guessing. Before the landing page replaces index.html.
probe="$out/getting-started/index.html"
[[ -f "$probe" ]] || { echo "docs: $probe is missing, cannot find the design-system stylesheet" >&2; exit 1; }
href="$(grep -o 'href="[^"]*/_static/ds[^"/]*\.css[^"]*"' "$probe" | grep -v 'ds-layers' | head -n1 | sed 's/^href="//; s/"$//; s/?.*$//')" || true
if [[ -z "$href" ]]; then
  echo "docs: $probe loads no design-system ds.css; build with FEATURE_NAVIGATION_PREVIEW=true" >&2
  exit 1
fi
css="$out/_static/${href##*/_static/}"
[[ -f "$css" ]] || { echo "docs: $css (from $href) does not exist" >&2; exit 1; }
# An incremental docs-builder build keeps the previous ds.css, theme and all: cut it at the marker before appending.
marker="/* snapshots-docs.css: appended by scripts/docs-finish.sh */"
awk -v m="$marker" '$0 == m { exit } { print }' "$css" > "$css.tmp"
{ printf '%s\n' "$marker"; cat "$root/docs/snapshots-docs.css"; } >> "$css.tmp"
mv "$css.tmp" "$css"

# Root links that skip htmx, and the header logo: see scripts/docs-pages.py. The logo is the landing page's nav lockup
# (156x48), not branding.icon, which docs-builder uses where a square mark fits.
python3 -I "$root/scripts/docs-pages.py" "$out" "$root/docs" "$prefix" images/snapshots-lockup-nav.svg 156 48

cp "$root/docs/snapshots-landing.html" "$out/index.html"
cp "$root/docs/robots.txt" "$out/robots.txt"
cp "$root/docs/CNAME" "$out/CNAME"
# docs-builder copies images, not fonts; the theme needs these (it ships Inter and JetBrains Mono itself).
mkdir -p "$out/fonts" && cp "$root"/docs/fonts/*.woff2 "$out/fonts/"
echo "docs: finished $out (theme appended to ${css#"$out"/}, landing page applied)"
