---
name: nullean-snapshots-http-assertions
description: >-
  Use when an AwesomeAssertions test needs to snapshot an API: one `HttpResponseMessage`, or a whole `.http` file run
  against a `WebApplicationFactory` client or a live host. Nullean.Snapshots.Http.Assertions gives
  `MatchHttpSnapshot`, `MatchHttpFileSnapshot` and `HttpFileTest.RunAsync`.
---

# Nullean.Snapshots.Http.Assertions

AwesomeAssertions extensions over `Nullean.Snapshots.Http`; the options types (`HttpSnapshotOptions`,
`HttpFileSnapshotOptions`) come from there. Without AwesomeAssertions, call `HttpFileSnapshot.Match` directly.

## The calls you need

`using Nullean.Snapshots.Http;`

```csharp
using var response = await client.GetAsync("/api/orders/3");
await response.Should().MatchHttpSnapshot();          // .snapshots/{dir}/{Class}/{Method}.verified.responses
```

```csharp
var transcript = await HttpFileSnapshot.RunAsync("http/orders.http", new() { Client = client });
transcript.Should().MatchHttpFileSnapshot();         // .snapshots/http/orders.verified.responses
```

One test case per file with `Nullean.Snapshots.TUnit`:

```csharp
[Test]
[HttpFiles("http")]
public async Task Run(SnapshotFile file)
{
	var options = new HttpFileSnapshotOptions { Client = factory.CreateClient(new() { AllowAutoRedirect = false }) };
	if (HttpFileTest.SkipReason(file, options) is { } why)
		Skip.Test(why);
	await HttpFileTest.RunAsync(file, options);
}
```

## Gotchas

- `MatchHttpSnapshot` on a response returns a `Task`. Without `await` a mismatch is never reported and the test passes.
- It fails unless the subject is an `HttpResponseMessage`; pass the response, not its content or a string.
- `MatchHttpFileSnapshot` is keyed by the `.http` file, not the test, and takes no `name`. Two tests over one file
  share one verified file; use `HttpFileSnapshotOptions.Variant` to keep them apart.
- `HttpFileTest.RunAsync` does not skip on `# @requires` or `# @mutates`. Check `SkipReason` yourself.
- A relative path is relative to the test project, not the working folder.
- A request that cannot be resolved or sent fails the test, naming it first. A 404 or 500 is recorded, not failed.
- `MatchHttpSnapshot` on a `string` expects `.responses` text, as `HttpSnapshot.RenderAsync` writes it.

Docs: https://snapshots.nullean.net/formats/http
