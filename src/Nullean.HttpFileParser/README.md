# Nullean.HttpFileParser

Parse, lint and run `.http` request files: the format of Visual Studio 17.8+, the VS Code REST Client and the
JetBrains HTTP Client, in its script-free forms. No dependencies beyond the BCL; AOT and trim clean. MIT.

```http
@host = https://localhost:5001

# @name login
POST {{host}}/login
Content-Type: application/json

{ "user": "ada", "password": "{{$processEnv SHOP_PASSWORD}}" }

###
GET {{host}}/orders?since={{$datetime iso8601 -1 d}}
Authorization: Bearer {{login.response.body.$.token}}
```

## Parse and lint

```csharp
using Nullean.HttpFileParser;

var file = HttpFile.Load("api.http");          // throws HttpFileParseException listing every problem
foreach (var request in file.Requests)
    Console.WriteLine($"{request.Line}: {request.RequestLine}");

var environment = HttpEnvironment.Load(file.Path, "dev");
foreach (var problem in file.Validate(environment))
    Console.WriteLine(problem);                 // api.http:12: unknown variable 'hots'
```

Every error is `file:line: message`, and parsing reports all of them at once.

## Run

```csharp
var result = await HttpFileRunner.RunAsync(file, client, new HttpFileRunOptions { Environment = "dev" });
foreach (var exchange in result.Exchanges.Select(result.Redact))
    Console.WriteLine($"{exchange.Request.RequestLine} -> {exchange.Response.StatusCode}");
if (result.Failure is { } failure)
    Console.WriteLine(failure);                 // api.http:7: Connection refused (localhost:5001)
```

- Requests run in file order. The run stops at the first request that cannot be resolved, sent or read, and keeps the
  exchanges before it. A 404 or 500 is a response, not a failure.
- A URL starting with `/` resolves against `HttpClient.BaseAddress`, so one file runs against a
  `WebApplicationFactory` client and, with `@host`, against a deployed host.
- The runner follows redirects itself so that `# @no-redirect` works per request. Give it a client that does not:
  `new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })`, or for `WebApplicationFactory`,
  `factory.CreateClient(new() { AllowAutoRedirect = false })`. With no client, it creates one that decompresses and
  keeps a cookie jar (`# @no-cookie-jar` opts a request out).
- A body a client left compressed (`gzip`, `deflate`, `br`) is decoded; `Response.ContentEncoding` says which.
- Each exchange holds the request as written, the request as sent, the response and its body bytes.

## What it understands

- Requests separated by `###` (`### name` names the next request), `# @name login`, `#` and `//` comments.
- `METHOD URL [HTTP/1.1|HTTP/2|HTTP/3]`, `GET` when the method is left out, `?`/`&` and indented continuation lines.
- Headers, a blank line, then the body. `< ./file` sends a file, `<@ ./file` substitutes variables in it first,
  and `multipart/*` bodies can mix text and `< ./file` lines.
- File variables `@name = value`, visible to what follows them.
- `http-client.env.json` environments, found in the file's folder or above: `$shared` defaults, the named
  environment, then `http-client.env.json.user` or `http-client.private.env.json` on top. File variables beat them;
  `HttpFileRunOptions.Variables` beats everything.
- `{{$guid}}`, `{{$uuid}}`, `{{$random.uuid}}`, `{{$randomInt [min max]}}` (max exclusive), `{{$datetime ...}}`,
  `{{$localDatetime ...}}`, `{{$timestamp}}`, `{{$isoTimestamp}}` with offsets such as `-1 d`, `{{$processEnv NAME}}`,
  `{{$env.NAME}}`, `{{$dotenv NAME}}` and `{{$secret Name}}`.
- `{{login.response.body.$.token}}`, `{{login.response.body.*}}`, `{{login.response.headers.Location}}` and the same
  with `request`. JSONPath supports `$`, `.name`, `["name"]` and `[n]`.
- `# @no-redirect`, `# @no-cookie-jar`, `# @timeout 30`.

Refused with a reason: JetBrains scripts (`> {% %}`, `< {% %}`, `> handler.js`), response redirection (`>> file`),
`@prompt`, `$aadToken`, GraphQL requests, cURL commands and XPath.

## Secrets and redaction

Environment entries such as `{ "provider": "AspnetUserSecrets", "secretName": "Shop:ApiKey" }` are read from .NET user
secrets. The id comes from `HttpFileRunOptions.UserSecretsId`, else the `[UserSecretsId]` attribute of
`SecretsAssembly`, else `<UserSecretsId>` in the nearest project file above the `.http` file.
`HttpFileRunOptions.Secrets` is asked first, and is the source for other providers and `{{$secret Name}}`.

Every value from a secret, `$processEnv`, `$env`, `$dotenv` or a `.user`/`.private` environment file is recorded.
`result.Redact(text)` and `result.Redact(exchange)` put the reference back (`{{$processEnv SHOP_PASSWORD}}`),
longest value first; failure messages are already redacted. A sensitive value shorter than 4 characters fails the run,
because replacing it everywhere would mangle unrelated text.

## Deterministic runs

Pass `TimeProvider` and a seeded `Random` in `HttpFileRunOptions` to make `$datetime`, `$guid` and `$randomInt`
repeatable.
