# Nullean.Snapshots.PublicApi

![snapshots.](https://raw.githubusercontent.com/nullean/snapshots/main/docs/images/brand/png/lockup-light.png)

Public API snapshots for Nullean.Snapshots: an assembly's public API stored as a `.api` file, C# one declaration per line, and every change reported by path with the version change it calls for, who it breaks and where in the verified file.

[![NuGet](https://img.shields.io/nuget/v/Nullean.Snapshots.PublicApi?style=flat-square)](https://www.nuget.org/packages/Nullean.Snapshots.PublicApi)

## Install

```sh
dotnet add package Nullean.Snapshots.PublicApi
```

## Quick start

```csharp
using AwesomeAssertions;
using Nullean.Snapshots.PublicApi;

public class ApiTests
{
	[Test]
	public void Public_api_is_unchanged() => typeof(Widget).Assembly.Should().MatchApiSnapshot();
}
```

The first run writes `ApiTests/Public_api_is_unchanged.received.api`. Accept it, and from then on a change to the API fails the test until the new file is accepted:

```
Public API did not match snapshot: tests/Acme.Tests/.snapshots/ApiTests/Public_api_is_unchanged.verified.api
Suggested version change: Major (2 breaking)
2 difference(s):
Lib.Drawing › IShape › Resize(double)  added void Resize(double factor)  [major: breaks implementers, compiled code and source]   (verified.api:4)
Lib.Drawing › Widget › Name(int) › parameter 1 name  index → position  [major: breaks callers, source only] (callers that pass it by name)   (verified.api:12)
```

The verified file reads like the code:

```
assembly Lib
using interface System: IDisposable

namespace Lib.Drawing
  interface IShape : IDisposable
    double Area { get; }
    void Draw()

  class Widget : IShape
    Widget(string name)
    double Area { get; }
    void Draw()
    string Name(int index)
```

## Options

- `split: ApiSplit.Namespace` (or `TopLevelNamespace`) keeps a snapshot per namespace, named after it, for a large assembly.
- `comparison: new ApiComparisonOptions { NotExtensionPoints = ["Lib.Internal.*"] }` names types nobody outside implements or derives from: a change that only breaks their implementers is listed but does not ask for a major version. A type with an attribute named `InternalImplementationOnlyAttribute` counts as one too.

The reading, writing and comparing come from [Nullean.PublicApi](https://www.nuget.org/packages/Nullean.PublicApi) and [Nullean.PublicApi.Diff](https://www.nuget.org/packages/Nullean.PublicApi.Diff), the engine of [assembly-differ](https://github.com/nullean/assembly-differ), whose `api --verify` checks the same files from a build.
