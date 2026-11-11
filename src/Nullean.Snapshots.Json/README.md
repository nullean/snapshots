# Nullean.Snapshots.Json

![snapshots.](https://raw.githubusercontent.com/nullean/snapshots/main/docs/images/brand/png/lockup-light.png)

JSON snapshots and inline JSON assertions for Nullean.Snapshots, compared as trees: key order ignored, numbers by value, differences reported by path with the line and column in the verified file.

[![NuGet](https://img.shields.io/nuget/v/Nullean.Snapshots.Json?style=flat-square)](https://www.nuget.org/packages/Nullean.Snapshots.Json)

## Install

```sh
dotnet add package Nullean.Snapshots.Json
```

## Quick start

```csharp
using System.Text.Json.Serialization;
using AwesomeAssertions;
using Nullean.Snapshots.Json;
using Nullean.Snapshots.Trees;

public record Order(int Id, string Product, DateTimeOffset CreatedAt);

[JsonSerializable(typeof(Order))]
public partial class AppJson : JsonSerializerContext;

public class OrderTests
{
	[Test]
	public void Serializes()
	{
		var order = new Order(3, "kibana", DateTimeOffset.UtcNow);
		var diff = new TreeDiffOptions { IgnorePaths = ["$.CreatedAt"] };
		order.Should().MatchJsonSnapshot(AppJson.Default.Order, diff); // source-generated: AOT-safe
	}

	[Test]
	public void Inline() =>
		"""{ "name": "Kibana", "id": 2 }""".Should().MatchInlineJsonSnapshot("""
			{ "id": 2, "name": "Kibana" }
			""");
}
```

- `MatchJsonSnapshot` takes JSON text, or an object with a `JsonTypeInfo<T>` (source-generated, AOT-safe) or
  `JsonSerializerOptions` (reflection).
- `TreeDiffOptions` ignores volatile paths (`$.meta.generatedAt`, `$..id`) or makes key order and number form count
  (`StrictNumbers`).
- `BeJson` and `MatchInlineJsonSnapshot` compare with JSON written in the test.
- Use the text `MatchSnapshot` instead when the exact bytes matter, such as whitespace or key order on the wire.

## Learn more

- Documentation: https://snapshots.nullean.net
- JSON snapshots: https://snapshots.nullean.net/formats/json/
- Source and issues: https://github.com/nullean/snapshots
