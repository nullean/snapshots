#!/usr/bin/env bash
# Smoke-tests an installed snap: builds a small fixture repository and runs status, diff, accept and reject on it.
# Usage: build/smoke-cli.sh <command that runs snap>...
#   build/smoke-cli.sh ./tool/snap
#   build/smoke-cli.sh dotnet dnx Nullean.Snapshots.Cli@1.2.3 --add-source ./feed --yes
# Runs on bash 3.2 (macOS) and Git Bash (Windows).
set -euo pipefail

if [ "$#" -eq 0 ]; then
  echo "usage: $0 <command that runs snap>..." >&2
  exit 2
fi

fixture="$(mktemp -d)"
trap 'rm -rf "$fixture"' EXIT

received="$fixture/.artifacts/.snapshots/received/Acme.Tests"
verified="$fixture/tests/Acme.Tests/.snapshots"
mkdir -p "$fixture/.git" "$received/Rendering/HtmlTests" "$verified/Rendering/HtmlTests"
printf '<Project />\n' > "$fixture/tests/Acme.Tests/Acme.Tests.csproj"
printf 'project=Acme.Tests\nverified=../../../../tests/Acme.Tests/.snapshots\n' > "$received/.snapshots-project"
printf 'a\nb\n' > "$verified/Rendering/HtmlTests/RendersTable.verified.txt"
printf 'a\nB\n' > "$received/Rendering/HtmlTests/RendersTable.received.txt"
printf 'new\n' > "$received/Rendering/HtmlTests/RendersList.received.txt"
printf 'drop\n' > "$received/Rendering/HtmlTests/Dropped.received.txt"

fail() { echo "SMOKE FAILED: $*" >&2; exit 1; }

# Runs snap with the given arguments; sets $out and $code.
run() {
  set +e
  out="$("$@" 2>&1)"
  code=$?
  set -e
  printf '$ %s\n%s\n[exit %s]\n' "$*" "$out" "$code"
}

run "$@" status --root "$fixture"
[ "$code" -eq 1 ] || fail "status should exit 1 with pending snapshots"
case "$out" in *"3 pending snapshot(s)"*) ;; *) fail "status should list 3 pending snapshots" ;; esac

run "$@" status --json --root "$fixture"
case "$out" in *'"state": "changed"'*) ;; *) fail "status --json should report a changed snapshot" ;; esac

run "$@" diff RendersTable --root "$fixture"
[ "$code" -eq 0 ] || fail "diff should exit 0"
case "$out" in *"+B"*) ;; *) fail "diff should show the added line" ;; esac

run "$@" reject Dropped --root "$fixture"
[ "$code" -eq 0 ] || fail "reject should exit 0"
[ ! -e "$received/Rendering/HtmlTests/Dropped.received.txt" ] || fail "reject should delete the received file"

run "$@" accept nothing-like-this --root "$fixture"
[ "$code" -eq 2 ] || fail "accept of an unknown key should exit 2"

run "$@" accept --all --root "$fixture"
[ "$code" -eq 0 ] || fail "accept --all should exit 0"
[ "$(cat "$verified/Rendering/HtmlTests/RendersTable.verified.txt")" = "$(printf 'a\nB')" ] || fail "accept should write the verified file"
[ -e "$verified/Rendering/HtmlTests/RendersList.verified.txt" ] || fail "accept should create the new verified file"

run "$@" status --root "$fixture"
[ "$code" -eq 0 ] || fail "status should exit 0 once everything is accepted"

echo "snap smoke test passed"
