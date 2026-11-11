# Nullean.Snapshots.Http

![snapshots.](https://raw.githubusercontent.com/nullean/snapshots/main/docs/images/brand/png/lockup-light.png)

HTTP snapshots for Nullean.Snapshots: snapshot an `HttpResponseMessage`, or run a `.http` file and compare every response, status, chosen headers and JSON bodies as trees, with secrets redacted.

[![NuGet](https://img.shields.io/nuget/v/Nullean.Snapshots.Http?style=flat-square)](https://www.nuget.org/packages/Nullean.Snapshots.Http)

## Install

```sh
dotnet add package Nullean.Snapshots.Http
```

## Quick start

Run a `.http` file and compare it with its verified file, without a test framework:

```csharp
using Nullean.Snapshots.Http;

public static class OrdersCheck
{
	public static async Task CheckAsync(HttpClient client)
	{
		var options = new HttpFileSnapshotOptions { Client = client, Environment = "test" };
		var transcript = await HttpFileSnapshot.RunAsync("http/orders.http", options);
		var result = HttpFileSnapshot.Match(transcript);           // .snapshots/http/orders.verified.responses
		if (!result.Passed)
			throw new InvalidOperationException(result.FailureMessage);
	}
}
```

- `HttpSnapshot.RenderAsync(response, new HttpSnapshotOptions { Headers = ["x-request-id"] })` renders one response.
- A live service: `Transcript = TranscriptMode.Shape` keeps only the shape, `ReadOnly = true` sends no writes, and
  `Variant` keeps one verified file per environment.
- In AwesomeAssertions tests use `Nullean.Snapshots.Http.Assertions`; from the command line, `snap http run` and
  `snap http compare`. Parsing and running `.http` files is `Nullean.HttpFileParser`.

## Learn more

- Documentation: https://snapshots.nullean.net
- HTTP snapshots: https://snapshots.nullean.net/formats/http/
- Expected differences: https://snapshots.nullean.net/workflow/expected-differences/
- Source and issues: https://github.com/nullean/snapshots
