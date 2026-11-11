# HTTP

```csharp
using Nullean.Snapshots.Http;
using Nullean.Snapshots.TUnit;

public class ShopApi(ShopFactory factory)       // your usual setup: a WebApplicationFactory, a fixture
{
    [Test]
    [HttpFiles("http")]                          // a test case per http/**/*.http
    public Task Run(SnapshotFile file) =>
        HttpFileTest.RunAsync(file, new() { Client = factory.CreateClient() });
}
```

Each `.http` file is its own test case, named after its file: `Run(orders)`, `Run(admin/users)`. The file is the one
Visual Studio, Rider and VS Code's REST Client already run, parsed and run by
[Nullean.HttpFileParser](https://github.com/nullean/snapshots/tree/main/src/Nullean.HttpFileParser):

```text
# http/orders.http
### login
POST {{host}}/api/login
Content-Type: application/json

{"user": "ada", "password": "{{$secret ShopPassword}}"}

###
GET {{host}}/api/orders
Authorization: Bearer {{login.response.body.$.token}}
```

Every request runs in order, so a later request can use an earlier response. An error status such as 404 or 500 is
a response like any other and is recorded, not a failure.

| Package | |
|---|---|
| `Nullean.Snapshots.Http` | the format, `HttpSnapshot.RenderAsync`, `HttpFileSnapshot.RunAsync` |
| `Nullean.Snapshots.Http.Assertions` | `MatchHttpSnapshot`, `MatchHttpFileSnapshot` and `HttpFileTest` for AwesomeAssertions |

## The format

Responses are stored as `.verified.responses`, one section per request:

```text
## login: POST {{host}}/api/login
status: 200 OK
content-type: application/json; charset=utf-8

{
  "token": "abc"
}

## GET {{host}}/api/orders
status: 404 Not Found
```

- A section is headed by the request **as written**, its variables unresolved, so neither a host nor a secret appears
  and the heading survives a change of host. A named request leads with its name.
- `status:` is the code and the reason phrase.
- Then the chosen headers, lower case, sorted by name, several values joined with `, `: `content-type` and `location`
  unless you choose more (see [options](#options)). `authorization`, `cookie`, `set-cookie` and `proxy-authorization`
  are never written, even when chosen.
- Then a blank line and the body, decoded:
  - JSON (`application/json` or any `+json` type) pretty-printed and compared as a tree: keys in any order, numbers by
    value. Invalid JSON is written as text.
  - A server-sent event stream (`text/event-stream`, what an MCP server answers with) as `events:` and one block per event, a blank line
    between them: `event:`, `id:` and `retry:` lines, then `data:` with its JSON pretty-printed on the lines after it (compared as a tree, so a
    changed field is `body[0].data.result.n`, and `IgnorePaths` and `body $[*].data...` rules reach it) or text after `data:` when it is not JSON,
    such as `[DONE]`. A stream with a comment line, a repeated field, empty data or several lines of data that is not JSON is quoted as text, below.
    A [shape](#shapes-properties-without-values) of a stream is `text: text/event-stream`.
  - Any other text (`text/*`, XML, JavaScript, form posts, anything with a charset) with `> ` before every line, so a
    body can never start a section.
  - Anything else as `binary: 2048 bytes, image/png`.

Sections are compared as trees, so an inserted request is one difference, and a changed field names its path:

```text
HTTP did not match snapshot: tests/Shop.Tests/.snapshots/http/orders.verified.responses
1 difference(s):
$[0].body.token   expected "abc"   actual "xyz"   (verified.responses:6:12)
```

followed by a line diff. Header order never matters.

## One response

`MatchHttpSnapshot` snapshots a single `HttpResponseMessage`, keyed by the test like any snapshot:

```csharp
using var response = await client.GetAsync("/api/orders/3");

await response.Should().MatchHttpSnapshot();
```

The section is headed by the request's method, path and query (`## GET /api/orders/3`), or `## response` for a
response with no request. `HttpSnapshot.RenderAsync(response)` gives the text without asserting.

## `.http` files

The snapshot belongs to the **file**, not the test: `http/orders.http` is compared with
`.snapshots/http/orders.verified.responses`, so a test and [`snap http run`](../workflow/snap.md#http-files) share it.

```csharp
var transcript = await HttpFileSnapshot.RunAsync("http/orders.http", new() { Client = client });
transcript.Should().MatchHttpFileSnapshot();
```

`HttpFileTest.RunAsync(file, options)` does both, and `[HttpFiles(folder)]` (Nullean.Snapshots.TUnit) yields a test
case per file, `http` by default. A folder that needs other setup gets its own class with a more specific folder, as
with [journeys](browser.md#setup-per-folder).

## Volatile values

`HttpFileTest.RunAsync`, `MatchHttpFileSnapshot` and `MatchHttpSnapshot` take the same `SnapshotOptions` as every other assertion (scrubbers are text and
run on the whole transcript) and a `TreeDiffOptions` for the trees. The paths are rooted at the sections of the file, so a score in every hit of every
response is `$[*].body.hits[*].score`:

```csharp
await HttpFileTest.RunAsync(
	file,
	new() { Client = client },
	diff: new TreeDiffOptions { IgnorePaths = ["$[*].body.hits[*].score"] }
);
```

A value that is ignored must still be present and of the same kind; use [shape mode](#shapes-properties-without-values) to compare only the structure.

A URL starting with `/` resolves against the client's `BaseAddress`, so one file runs in memory against a
`WebApplicationFactory` client and against a real host through `{{host}}`. Give `host` an empty value in the
environment the test uses and both give the same verified file.

A request that cannot be resolved, sent or read stops the run. The assertion fails, naming the request first, and the
received file holds the responses before it:

```text
Request stopped at http/orders.http:9: GET {{host}}/api/orders
Connection refused (localhost:5000)
```

## Expectations

A comment before a request can state what its response must be. The runner checks each one after the response and
stops the run at the first that does not hold, in either [transcript mode](#a-live-service-and-variants):

```text
# @expect status 200
# @expect header content-type ~ json
# @expect json $.result.tools[*].name contains "search_docs"
POST {{host}}/docs/_mcp
Content-Type: application/json

{"jsonrpc": "2.0", "id": 1, "method": "tools/list"}
```

| Directive | Holds when |
|---|---|
| `@expect status 200` | the status code is 200; `2xx` accepts any code of that class |
| `@expect header content-type ~ json` | one value of the header contains the text, ignoring case |
| `@expect json $.user.name equals ada` | the JSONPath selects values and every one equals the text |
| `@expect json $.user.roles[*] contains admin` | one selected value contains the text; an array contains an element equal to it |

A value is the rest of the line, or a JSON string (`"two words"`) when it has leading spaces or quotes. A JSONPath may
use `[*]` and `.*` to select every element or property value. A failure names the request, the directive's line and what
the response had; the response itself stays in the transcript:

```text
Request stopped at http/tools.http:2: POST {{host}}/docs/_mcp
@expect json $.result.tools[*].name contains "search_docs" failed: $.result.tools[*].name selected "ask_docs", "find_docs"
```

## Read-only runs

`ReadOnly = true` (`--read-only` on `snap http run`) is for a live service: a request whose method is not GET, HEAD or
OPTIONS is not sent, and the run stops at it. A request that only reads over POST, such as a JSON-RPC call, says so with
`# @readonly` and is sent:

```text
# @readonly
# @expect json $.result.tools[*].name contains search_docs
POST {{host}}/docs/_mcp
```

## Files that apply to some services

Comments before the first request can carry the same `@` directives as a [journey's header](browser.md#journeys-that-apply-to-some-sites):

```text
# @tags mcp smoke
# @requires !env:prod
# @monitor schedule=10 locations=germany,us_east
# @alert pagerduty
# @mutates
@host = https://example.test

### health
GET {{host}}/health
```

| Directive | |
|---|---|
| `@tags <name...>` | names for selecting files: lower case letters, digits, `.`, `_`, `-` |
| `@requires <condition...>` | all must hold for the file to apply: a feature flag, `build-type:<a,b>` or `env:<a,b>`, each optionally with a leading `!`. Something the run does not know is not matched, so a plain condition fails and a negated one holds |
| `@monitor [schedule=<minutes>] [locations=<a,b>]` | the file is exported as a monitor |
| `@alert <channel...>` | where a failing monitor alerts, such as `pagerduty` |
| `@mutates` | the file changes data on the service, so a read-only run skips it |

They belong in the first block, before the first request; one anywhere else is an error with its line. Any other `@`
word (`@note`, `@no-log`) stays a plain comment, as in the other dialects. `HttpFile.Metadata` holds what the header said.

Tell the run what the service is with `Features`, `BuildType` and `Env` on the options, and ask whether a file applies
before running it:

```csharp
var service = new HttpFileSnapshotOptions { Client = client, Env = "edge", ReadOnly = true };

[Test]
[HttpFiles("http")]
public Task Run(SnapshotFile file)
{
    if (HttpFileTest.SkipReason(file, service) is { } why)
        Skip.Test(why);              // TUnit; any framework's skip works
    return HttpFileTest.RunAsync(file, service);
}
```

`[HttpFiles]` also gives each case its `@tags` as TUnit categories, so `--treenode-filter "/*/*/*/*[Category=mcp]"`
selects by tag; see [select journeys by tag](browser.md#select-journeys-by-tag).

`HttpFileTest.Unmet(file, context)` returns the first `@requires` condition the context does not meet, as written
(`@requires !env:prod`), or `null`. `SkipReason` does the same from the options and also skips a `@mutates` file on a
read-only run.

## Secrets and redaction

Values reach a request from `http-client.env.json` (with `$shared`, a selected environment, and the private
`http-client.env.json.user` or `http-client.private.env.json` overrides), `{{$dotenv NAME}}`, `{{$processEnv NAME}}`,
`{{$secret Name}}`, ASP.NET Core user secrets and file variables. Every value from a secret, the process environment,
a `.env` file or a private environment file is replaced by the reference that produced it before anything is
written, including when a server echoes it back in a body or a header:

```text
## POST {{host}}/api/echo
status: 200 OK
content-type: application/json; charset=utf-8
x-echo: {{$secret ApiKey}}

{
  "key": "{{$secret ApiKey}}"
}
```

Secrets a test already has, from configuration or a fixture, come in through `Secrets`:

```csharp
var configuration = factory.Services.GetRequiredService<IConfiguration>();
await HttpFileTest.RunAsync(file, new()
{
    Client = factory.CreateClient(),
    Secrets = name => configuration[name],
});
```

`Secrets` answers `{{$secret Name}}`, is asked before user secrets for an environment entry with the
`AspnetUserSecrets` provider, and is the only source for the other providers. User secrets come from
`UserSecretsId`, else the `[UserSecretsId]` of `SecretsAssembly` (typically the app's assembly), else
`<UserSecretsId>` in the nearest project file above the `.http` file.

## Compression

Bodies are always written decoded. The runner's own client decompresses gzip, deflate and brotli; with a client of
your own that does not, the runner decodes a body still marked `Content-Encoding`, and so does
`HttpSnapshot.RenderAsync`. `content-encoding` is written only when you choose it.

## Redirects

The runner follows redirects itself, so `# @no-redirect` works per request. A client of your own should not follow
them too: create it with `AllowAutoRedirect = false`. A client that follows redirects makes `@no-redirect` ineffective;
this is not detected. `WebApplicationFactory.CreateClient()` follows redirects by default, so pass
`new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }`.

## Options

| `HttpFileSnapshotOptions` | `snap http run` | |
|---|---|---|
| `Client` | | the `HttpClient` to send with; the runner's own when unset |
| `Environment` | `--env` | the environment to select from `http-client.env.json` |
| `Variables` | `--var name=value` | beat file variables and the environment |
| `Headers` | `--header` | headers to write besides `content-type` and `location` |
| `Secrets` | | answers `{{$secret Name}}` and secret providers by name |
| `UserSecretsId`, `SecretsAssembly`, `UserSecretsRoot` | | where user secrets are read |
| `TimeProvider`, `Random` | | the clock and randomness for `$datetime`, `$guid` and the like |
| `Transcript` | `--transcript compare\|off\|shape` | `Off` sends the requests and compares no transcript; `Shape` compares only the properties of JSON bodies; see [a live service](#a-live-service-and-variants) and [shapes](#shapes-properties-without-values) |
| `Variant` | `--variant <name>` | compares with `{file}.{variant}.verified.responses`; see [a live service](#a-live-service-and-variants) |
| `ReadOnly` | `--read-only` | refuses any request that is not GET, HEAD or OPTIONS unless it carries `# @readonly`; see [read-only runs](#read-only-runs) |
| `Features`, `BuildType`, `Env` | | what `# @requires` is judged against; see [files that apply](#files-that-apply-to-some-services) |

`HttpSnapshotOptions.Headers` chooses headers for `MatchHttpSnapshot` and `RenderAsync`; `HttpSnapshotOptions.Shape` writes the
body as its [shape](#shapes-properties-without-values).

## A live service and variants

A live service's responses drift, so a transcript compared with a verified file fails for reasons that are not bugs.
`Transcript = TranscriptMode.Off` sends every request and fails the test on a request that fails (a refused connection,
a timeout, an unresolved variable), without comparing the transcript or needing a verified file. An error status such as
404 is still a response, not a failure.

`Variant = "edge"` compares `http/orders.http` with `.snapshots/http/orders.edge.verified.responses` and leaves
`orders.verified.responses` alone, for responses that legitimately differ per environment or flag set. A variant is
letters, digits, `.`, `-` and `_`.

## Shapes: properties without values

`Transcript = TranscriptMode.Shape` (or `--transcript shape`) writes every JSON body as its shape, so the verified file
says which properties a response has and of what kind, and a changed value never fails it. A property added or dropped,
or one that changed kind, is one difference each.

```
## GET {{host}}/api/orders
status: 200 OK
content-type: application/json; charset=utf-8

{
  "customer": {
    "name": "string",
    "vip": null
  },
  "lines": [
    {
      "note": "string",
      "qty": 1,
      "sku": "string"
    }
  ],
  "open": false,
  "price": 1.0,
  "total": 1
}
```

- Strings become `"string"`, booleans `false`, integers `1`, floats `1.0`; `null` stays `null`. Keys are sorted.
- An array collapses to its distinct shapes: all its objects merge into one with the union of their keys, so the number
  of items never shows. A key that is `null` in one item and something else in another takes the something else.
  Items that disagree about a key's kind (a string in one, an integer in the other) stay separate entries. An empty
  array stays `[]`, so a list that was emptied is a difference.
- A text body is written as `text: {media type}` and a binary body as before. Status and the chosen headers are written as
  usual.
- Only `.http` runs and `HttpSnapshotOptions.Shape` have a shape; a journey rejects `TranscriptMode.Shape`.

## CLI

```sh
snap http run 'tests/Shop.Tests/http/**/*.http' --env dev --var host=http://localhost:5000
```

runs the files with the runner's own client and compares each transcript with the verified file, as a test does. It
writes received files for `snap review` and never accepts. See [`snap http run`](../workflow/snap.md#http-files).
