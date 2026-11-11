# Nullean.Snapshots.Http.Assertions

![snapshots.](https://raw.githubusercontent.com/nullean/snapshots/main/docs/images/brand/png/lockup-light.png)

AwesomeAssertions extensions for Nullean.Snapshots.Http: `MatchHttpSnapshot` for a response, `MatchHttpFileSnapshot` and `HttpFileTest` for `.http` files, compared as trees with secrets redacted.

[![NuGet](https://img.shields.io/nuget/v/Nullean.Snapshots.Http.Assertions?style=flat-square)](https://www.nuget.org/packages/Nullean.Snapshots.Http.Assertions)

## Install

```sh
dotnet add package Nullean.Snapshots.Http.Assertions
```

## Quick start

```csharp
using AwesomeAssertions;
using Nullean.Snapshots;
using Nullean.Snapshots.Http;
using Nullean.Snapshots.TUnit;

public class OrdersApiTests
{
	private static readonly HttpClient Client = new() { BaseAddress = new Uri("http://localhost:5000/") };

	[Test]
	public async Task GetsAnOrder()
	{
		using var response = await Client.GetAsync("/api/orders/3");
		await response.Should().MatchHttpSnapshot();               // status, chosen headers, JSON body as a tree
	}

	[Test]
	[HttpFiles("http")]                                           // one test case per .http file
	public Task Run(SnapshotFile file) =>
		HttpFileTest.RunAsync(file, new HttpFileSnapshotOptions { Client = Client });
}
```

`[HttpFiles]` is in `Nullean.Snapshots.TUnit`. For `WebApplicationFactory`, give the runner a client that does
not follow redirects, `factory.CreateClient(new() { AllowAutoRedirect = false })`, so `# @no-redirect` works per
request; `HttpFileTest.SkipReason` says when a file does not apply.

## Learn more

- Documentation: https://snapshots.nullean.net
- HTTP snapshots: https://snapshots.nullean.net/formats/http/
- Source and issues: https://github.com/nullean/snapshots
