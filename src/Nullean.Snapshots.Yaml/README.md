# Nullean.Snapshots.Yaml

![snapshots.](https://raw.githubusercontent.com/nullean/snapshots/main/docs/images/brand/png/lockup-light.png)

YAML snapshots and inline YAML assertions for Nullean.Snapshots: stored as written, comments and layout kept, compared as trees with the YAML 1.2 core schema, differences pointing at line and column.

[![NuGet](https://img.shields.io/nuget/v/Nullean.Snapshots.Yaml?style=flat-square)](https://www.nuget.org/packages/Nullean.Snapshots.Yaml)

## Install

```sh
dotnet add package Nullean.Snapshots.Yaml
```

## Quick start

```csharp
using AwesomeAssertions;
using Nullean.Snapshots.Trees;
using Nullean.Snapshots.Yaml;

public class ManifestTests
{
	[Test]
	public void Renders() =>
		Manifest.Render().Should().MatchYamlSnapshot();      // .verified.yml, stored as written

	[Test]
	public void IgnoresVersion() =>
		Manifest.Render().Should().MatchYamlSnapshot(
			new TreeDiffOptions { IgnorePaths = ["$.metadata.resourceVersion"] });

	[Test]
	public void Inline() =>
		Manifest.Render().Should().BeYaml("items: [{id: 1}]");
}
```

- Anchors and merge keys are resolved before comparing; `MapOrder.Significant` makes key order count.
- Use the text `MatchSnapshot` instead when formatting or comments are what you are testing.

## Learn more

- Documentation: https://snapshots.nullean.net
- YAML snapshots: https://snapshots.nullean.net/formats/yaml/
- Source and issues: https://github.com/nullean/snapshots
