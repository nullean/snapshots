# Nullean.Snapshots.Cli

![snapshots.](https://raw.githubusercontent.com/nullean/snapshots/main/docs/images/brand/png/lockup-light.png)

`snap`, the native AOT command line tool for Nullean.Snapshots: review, diff, accept and reject pending snapshots, find orphaned ones, and run browser journeys and `.http` files without a test run.

[![NuGet](https://img.shields.io/nuget/v/Nullean.Snapshots.Cli?style=flat-square)](https://www.nuget.org/packages/Nullean.Snapshots.Cli)

## Install

```sh
dotnet tool install --global Nullean.Snapshots.Cli
```

## Quick start

```sh
snap                               # walk through what changed: accept, reject or skip each snapshot
snap status --json                 # pending snapshots; exits 1 when there are any
snap diff CatalogTests/Renders     # full key, unique suffix, or glob
snap accept CatalogTests/Renders   # only after reading the diff
snap reject --all                  # delete received files, keep verified
snap orphans                       # verified files the last test run did not use
```

Without installing it: `dnx Nullean.Snapshots.Cli accept CatalogTests/Renders`.

## Journeys, `.http` files and two sites

```sh
snap journey run 'tests/Shop.Tests/journeys/**/*.journey' --base-url http://localhost:5000/
snap http run 'tests/Shop.Tests/http/**/*.http' --env dev --var host=http://localhost:5000
snap journey compare 'journeys/**/*.journey' --baseline https://prod.example.com/ --target https://preview.example.com/
snap http compare 'http/**/*.http' --baseline https://api.example.com --target https://api-preview.example.com
```

`compare` fails only on differences no rule in the checked-in `{file}.differences` expects; `--record` appends the
rules that would explain a failure instead.

Accepting an inline snapshot edits the string literal in the test's `.cs` file, so read `git diff` of the test files
as well as the verified ones.

## Learn more

- Documentation: https://snapshots.nullean.net
- The snap CLI: https://snapshots.nullean.net/workflow/snap/
- Expected differences: https://snapshots.nullean.net/workflow/expected-differences/
- Source and issues: https://github.com/nullean/snapshots
