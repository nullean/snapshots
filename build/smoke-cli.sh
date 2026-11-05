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
printf 'old\n' > "$verified/Rendering/HtmlTests/Gone.verified.txt"
printf '# verified files touched by the test run that started 2026-11-03T15:00:00Z\nRendering/HtmlTests/RendersTable.verified.txt\nRendering/HtmlTests/RendersList.verified.txt\n' > "$received/.snapshots-manifest"

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

# review reads one answer per line when its input is redirected: skip everything, then quit.
set +e
out="$(printf 's\ns\nq\n' | "$@" review --root "$fixture" 2>&1)"
code=$?
set -e
printf '$ %s review (scripted)\n%s\n[exit %s]\n' "$*" "$out" "$code"
[ "$code" -eq 1 ] || fail "review that skips everything should exit 1"
case "$out" in *"0 accepted, 0 rejected"*) ;; *) fail "review should report what it did" ;; esac

run "$@" orphans --root "$fixture"
[ "$code" -eq 1 ] || fail "orphans should exit 1 with an orphan"
case "$out" in *"Gone.verified.txt"*) ;; *) fail "orphans should list the untouched verified file" ;; esac

# A binary snapshot: a PNG signature and chunk length, full of CR, LF and NUL bytes that text handling would rewrite.
mkdir -p "$received/Rendering/ChartTests" "$verified/Rendering/ChartTests"
printf '\211PNG\r\n\032\n\000\000\000\015\015\012\015' > "$fixture/expected.png"
cp "$fixture/expected.png" "$received/Rendering/ChartTests/Draws.received.png"
printf 'diff' > "$received/Rendering/ChartTests/Draws.received-diff.png"
printf '\211PNG\r\n\032\n\000' > "$verified/Rendering/ChartTests/Draws.verified.png"

run "$@" diff ChartTests/Draws --root "$fixture"
[ "$code" -eq 0 ] || fail "diff of a binary snapshot should exit 0"
case "$out" in *"Binary snapshot"*"Draws.received-diff.png"*) ;; *) fail "diff should name the binary snapshot's diff image" ;; esac

run "$@" accept ChartTests/Draws --root "$fixture"
[ "$code" -eq 0 ] || fail "accept of a binary snapshot should exit 0"
cmp -s "$fixture/expected.png" "$verified/Rendering/ChartTests/Draws.verified.png" || fail "accept should copy a binary snapshot byte for byte"
[ ! -e "$received/Rendering/ChartTests/Draws.received-diff.png" ] || fail "accept should delete the diff image"

run "$@" accept nothing-like-this --root "$fixture"
[ "$code" -eq 2 ] || fail "accept of an unknown key should exit 2"

run "$@" accept --all --root "$fixture"
[ "$code" -eq 0 ] || fail "accept --all should exit 0"
[ "$(cat "$verified/Rendering/HtmlTests/RendersTable.verified.txt")" = "$(printf 'a\nB')" ] || fail "accept should write the verified file"
[ -e "$verified/Rendering/HtmlTests/RendersList.verified.txt" ] || fail "accept should create the new verified file"

run "$@" status --root "$fixture"
[ "$code" -eq 0 ] || fail "status should exit 0 once everything is accepted"

# An inline snapshot: the received record names a test file and the line of the call, and accepting rewrites the string
# literal there. The record carries the file's hash, as the library writes it, so the same check runs here.
sha() { if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi; }
mkdir -p "$fixture/tests/Acme.Tests/Rendering"
source="$fixture/tests/Acme.Tests/Rendering/HtmlTests.cs"
cat > "$source" <<'EOF_SOURCE'
class HtmlTests
{
    void A() => x.Should().MatchInlineSnapshot("one");
}
EOF_SOURCE
cat > "$received/Rendering/HtmlTests/A.L3.received.inline" <<EOF_RECORD
{
  "format": "Text",
  "method": "MatchInlineSnapshot",
  "source": "../../../../tests/Acme.Tests/Rendering/HtmlTests.cs",
  "sourceHash": "$(sha "$source")",
  "line": 3,
  "expression": "\"one\"",
  "expected": "one",
  "actual": "1\n2\n"
}
EOF_RECORD

run "$@" status --root "$fixture"
[ "$code" -eq 1 ] || fail "status should exit 1 with a pending inline snapshot"
case "$out" in *"A.L3  .inline"*) ;; *) fail "status should list the inline snapshot" ;; esac

run "$@" diff A.L3 --root "$fixture"
[ "$code" -eq 0 ] || fail "diff of an inline snapshot should exit 0"
case "$out" in *"HtmlTests.cs:3 (inline)"*"+2"*) ;; *) fail "diff should show the literal against the received value" ;; esac

run "$@" accept A.L3 --root "$fixture"
[ "$code" -eq 0 ] || fail "accept of an inline snapshot should exit 0"
case "$(cat "$source")" in *'MatchInlineSnapshot("""'*) ;; *) fail "accept should rewrite the literal as a raw string" ;; esac
[ ! -e "$received/Rendering/HtmlTests/A.L3.received.inline" ] || fail "accept should delete the inline record"

# The file changed since the test ran: nothing is edited, the record stays, and the command says so.
cat > "$received/Rendering/HtmlTests/A.L3.received.inline" <<EOF_RECORD
{
  "format": "Text",
  "method": "MatchInlineSnapshot",
  "source": "../../../../tests/Acme.Tests/Rendering/HtmlTests.cs",
  "sourceHash": "0000000000000000000000000000000000000000000000000000000000000000",
  "line": 3,
  "expression": "\"one\"",
  "expected": "one",
  "actual": "3\n"
}
EOF_RECORD
before="$(cat "$source")"
run "$@" accept A.L3 --root "$fixture"
[ "$code" -eq 1 ] || fail "accept of an inline snapshot whose file changed should exit 1"
case "$out" in *"the file changed since the test ran"*) ;; *) fail "accept should say why it refused" ;; esac
[ "$(cat "$source")" = "$before" ] || fail "a refused inline accept should leave the source alone"
run "$@" reject A.L3 --root "$fixture"
[ "$code" -eq 0 ] || fail "reject of an inline snapshot should exit 0"

# snap http run needs no server to fail: a refused connection stops the run, the failure comes first, and the partial
# transcript is written for review, never accepted.
mkdir -p "$fixture/tests/Acme.Tests/http"
printf 'GET {{host}}/never\n' > "$fixture/tests/Acme.Tests/http/down.http"
export SNAPSHOT_ACCEPT=1
run "$@" http run 'tests/**/*.http' --var host=http://127.0.0.1:9 --root "$fixture"
unset SNAPSHOT_ACCEPT
[ "$code" -eq 1 ] || fail "http run should exit 1 when a request fails"
case "$out" in *"Request stopped at "*"down.http:1: GET {{host}}/never"*) ;; *) fail "http run should name the failed request first" ;; esac
case "$out" in *"SNAPSHOT_ACCEPT is ignored"*) ;; *) fail "http run should say it never accepts" ;; esac
[ -e "$received/http/down.received.responses" ] || fail "http run should write the received transcript"
[ ! -e "$verified/http/down.verified.responses" ] || fail "http run should never accept"

run "$@" http run 'tests/**/*.http' --var nope --root "$fixture"
[ "$code" -eq 2 ] || fail "http run with a malformed --var should exit 2"

echo "snap smoke test passed"
