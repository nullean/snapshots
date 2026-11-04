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

A URL starting with `/` resolves against the client's `BaseAddress`, so one file runs in memory against a
`WebApplicationFactory` client and against a real host through `{{host}}`. Give `host` an empty value in the
environment the test uses and both give the same verified file.

A request that cannot be resolved, sent or read stops the run. The assertion fails, naming the request first, and the
received file holds the responses before it:

```text
Request stopped at http/orders.http:9: GET {{host}}/api/orders
Connection refused (localhost:5000)
```

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

`HttpSnapshotOptions.Headers` chooses headers for `MatchHttpSnapshot` and `RenderAsync`.

## CLI

```sh
snap http run 'tests/Shop.Tests/http/**/*.http' --env dev --var host=http://localhost:5000
```

runs the files with the runner's own client and compares each transcript with the verified file, as a test does. It
writes received files for `snap review` and never accepts. See [`snap http run`](../workflow/snap.md#http-files).
