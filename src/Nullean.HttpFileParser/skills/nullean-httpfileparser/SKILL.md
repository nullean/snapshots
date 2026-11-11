---
name: nullean-httpfileparser
description: >-
  Use when C# code needs to read, lint or send the requests in a `.http` file (Visual Studio, VS Code REST Client,
  JetBrains format), with `http-client.env.json` environments, user secrets and values chained from earlier
  responses. Nullean.HttpFileParser parses every error at once, runs the file against an `HttpClient` and redacts
  secrets from the results.
---

# Nullean.HttpFileParser

A standalone parser and runner for `.http` files, with no dependencies beyond the BCL. To snapshot a run's
responses in a test, use `Nullean.Snapshots.Http` instead of rendering them yourself.

## The calls you need

`using Nullean.HttpFileParser;`

```csharp
var file = HttpFile.Load("api/orders.http"); // HttpFileParseException: e.Errors, each "path:line: message"
```

Lint without sending anything:

```csharp
var environment = HttpEnvironment.Load(file.Path, "dev");
foreach (var problem in file.Validate(environment))
	Console.WriteLine(problem);
```

Run in file order:

```csharp
var result = await HttpFileRunner.RunAsync(file, client, new HttpFileRunOptions { Environment = "dev" });
foreach (var exchange in result.Exchanges.Select(result.Redact))
	Console.WriteLine($"{exchange.Request.RequestLine} -> {exchange.Response.StatusCode}");
if (result.Failure is { } failure)
	Console.WriteLine(failure);
```

Repeatable values and secrets from your own configuration:

```csharp
var options = new HttpFileRunOptions
{
	TimeProvider = fakeTime,
	Random = new Random(42),
	Secrets = name => configuration[name],
	ReadOnly = true,
};
```

## Gotchas

- `Succeeded` only means every request ran. A 404 or 500 is a response. Add `# @expect status 2xx` to fail on it.
- `HttpFile.Parse(text)` defaults the path to `request.http`, so body files and `http-client.env.json` resolve
  from the working folder. Pass the real path.
- Naming an `Environment` that no `http-client.env.json` defines throws `HttpFileParseException` from `RunAsync`.
  It is not a `Failure`.
- The runner follows redirects itself. Give it a client with `AllowAutoRedirect = false`, or `# @no-redirect` is
  ignored. Its cookie jar is used only when `client` is `null`.
- `Exchanges` hold raw secrets. Redact before logging; `Failure` messages are already redacted.
- A secret or env value shorter than 4 characters fails the run: too short to redact safely.
- `Validate` cannot check `$processEnv`, `$dotenv` or secrets. A timeout is a `Failure`; cancellation throws.
- `ReadOnly` refuses any request that is not GET, HEAD or OPTIONS unless it has `# @readonly`.
- JetBrains scripts, `>> file`, `@prompt` and GraphQL are parse errors, not ignored.

Docs: https://snapshots.nullean.net/formats/http
