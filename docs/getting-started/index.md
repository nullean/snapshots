# Getting started

## Install

Reference the assertion package for each format you snapshot, and the TUnit adapter:

```xml
<ItemGroup>
  <PackageReference Include="Nullean.Snapshots.AwesomeAssertions" Version="0.*" />
  <PackageReference Include="Nullean.Snapshots.Json" Version="0.*" />
  <PackageReference Include="Nullean.Snapshots.TUnit" Version="0.*" />
</ItemGroup>
```

Versions stay 0.x while the API settles. The packages need .NET 10.

The core package brings MSBuild targets along with it. They record where the test project lives, so a test run never
has to guess, and they show the verified files in your IDE. Nothing needs adding to `.gitignore`: received files go
to `obj/` or your artifacts folder, which are ignored already.

## The CLI

`snap` is a .NET tool, native AOT on Linux, macOS and Windows (x64 and arm64), with a portable fallback elsewhere:

```sh
dotnet tool install --global Nullean.Snapshots.Cli
snap status
```

Or run it without installing it:

```sh
dnx Nullean.Snapshots.Cli status
```

Next: [your first snapshot](first-snapshot.md).
