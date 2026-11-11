# Nullean.Snapshots: contributor and agent conventions

Snapshot testing on AwesomeAssertions for text, HTML, JSON, YAML and PNG images, plus a native AOT CLI, `snap`.
[DECISIONS.md](DECISIONS.md) records the non-obvious choices; read it before changing where files go or how they are
named.

## Build and test

[mise](https://mise.jdx.dev) installs the toolchain (`mise.toml`: the SDK from `global.json`, the .NET 10 runtime the
projects target, `gh`, `zizmor`) and runs the same task names as every nullean repository:

```sh
mise install       # once, and after the SDK in global.json changes
mise run build     # dotnet build -c Release
mise run test      # build, then every test project one at a time, as CI does
mise run pack      # build, then pack the libraries into .artifacts/package-output
mise run ci        # what CI's build job runs: build, test, pack, curb check
mise run format    # format the repository with curb
mise run check     # curb formatting check and zizmor on the workflows
mise run docs      # scripts/docs.sh: build and serve the docs (needs docs-builder)
mise run release   # show the release draft; `mise run release publish` releases
```

The plain `dotnet` commands work too:

```sh
dotnet build -c Release
dotnet test -c Release
dotnet test -c Release --project tests/Nullean.Snapshots.Tests --treenode-filter "/*/*/SnapshotTextTests/*"
```

Requires the .NET 11 SDK (pinned in `global.json`) and the .NET 10 runtime. Tests run on Microsoft.Testing.Platform
with TUnit.

This repository's own failure messages and CLI output are snapshots under `tests/*/.snapshots/`. When one fails, the
message says where the received file is. Read the diff, then accept with the command the message prints
(`SNAPSHOT_ACCEPT=1 dotnet test --project ... --treenode-filter ...`) or with the CLI:

```sh
dotnet run --project src/Nullean.Snapshots.Cli -- status
dotnet run --project src/Nullean.Snapshots.Cli -- accept <key>
```

`snap review` walks through pending snapshots one at a time; `snap orphans` lists verified files the last full test
run did not use. Always read `git diff` of the verified files before committing them. `build/smoke-cli.sh <snap command>` runs the
CLI end to end on a throwaway fixture.

## Releasing

release-drafter keeps a draft release current from the labels of merged pull requests, and every pull request carries
exactly one release label (`pr-labels.yml` enforces it). **Publishing the draft** releases: it creates the tag and runs
`ci.yml` on it, and once every gate has passed the `release` job pushes the packages to nuget.org and attaches them to
the release. Pushing a tag on its own does nothing. In 0.x a `breaking` label bumps the minor version; 1.0 is the
maintainer's call. See the [releasing](.claude/skills/releasing/SKILL.md) and
[breaking-changes](.claude/skills/breaking-changes/SKILL.md) skills, and `mise run release` for the current draft.

## Rules

- Failure messages are plain text: no ANSI, repo-relative paths, capped, readable by people and agents.
- Never pass snapshot content as a format string; braces in content must not break a message.
- Core has no heavy dependencies. Libraries set `IsAotCompatible`; an exception is documented in DECISIONS.md.
- The diff engine is tested with CsCheck properties first. Keep a failing seed as a regression test.
- Never accept snapshots automatically, and never in CI.
- Never publish a release (or the draft) without the owner's approval.
- Record non-obvious choices in DECISIONS.md in the same change.
- Every package ships an agent skill, `src/<Project>/skills/<name>/SKILL.md`. Change it with the public API, and
  compile its snippets against the package before committing.
