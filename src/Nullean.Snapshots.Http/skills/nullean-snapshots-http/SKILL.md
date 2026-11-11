---
name: nullean-snapshots-http
description: >-
  Use when C# code needs to snapshot HTTP responses without an assertion library: render an `HttpResponseMessage`
  as text, or run a `.http` file and compare its transcript with a verified file. Nullean.Snapshots.Http gives
  `HttpSnapshot`, `HttpFileSnapshot` and the `.responses` format, with secrets redacted and a shape mode for live
  services.
---

# Nullean.Snapshots.Http

The engine behind HTTP snapshots: rendering, `.http` runs and comparison results. In AwesomeAssertions tests use
`Nullean.Snapshots.Http.Assertions` (`MatchHttpSnapshot`, `HttpFileTest`) on top of it.

## The calls you need

`using Nullean.Snapshots;` and `using Nullean.Snapshots.Http;`

```csharp
var text = await HttpSnapshot.RenderAsync(response, new HttpSnapshotOptions { Headers = ["x-request-id"] });
```

Run a `.http` file and compare it with `.snapshots/http/orders.verified.responses`:

```csharp
var transcript = await HttpFileSnapshot.RunAsync("http/orders.http", new() { Client = client, Environment = "test" });
var result = HttpFileSnapshot.Match(transcript);
if (!result.Passed)
	throw new Exception(result.FailureMessage);
```

A live service: keep only the shape, send no writes, one verified file per environment:

```csharp
var live = new HttpFileSnapshotOptions { Transcript = TranscriptMode.Shape, ReadOnly = true, Variant = "prod", Env = "prod" };
if (HttpFileSnapshot.SkipReason("http/orders.http", live) is null)
	transcript = await HttpFileSnapshot.RunAsync("http/orders.http", live);
```

## Gotchas

- A relative `.http` path is relative to the test project, not the working folder.
- The verified file belongs to the `.http` file, not the test. Every test and `snap http run` over that file share
  it; `Variant` gives `{file}.{variant}.verified.responses`.
- `RunAsync` does not read `# @requires` or `# @mutates`. Call `SkipReason` first.
- A run that stopped at a failing request fails `Match` even when the partial transcript matches. A 404 is not a
  failure, it is recorded.
- `TranscriptMode.Off` compares nothing and needs no verified file, but failed requests and `# @expect` still fail.
- Only `content-type` and `location` are written by default. `authorization`, `cookie`, `set-cookie` and
  `proxy-authorization` are never written, even when listed in `Headers`.
- Headings show the request as written, variables unresolved, so a host change does not change the file.
- Your own `Client` should use `AllowAutoRedirect = false`; the runner follows redirects itself.

Docs: https://snapshots.nullean.net/formats/http
