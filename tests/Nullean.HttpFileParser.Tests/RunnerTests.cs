using System.Text;
using AwesomeAssertions;

namespace Nullean.HttpFileParser.Tests;

public class RunnerTests
{
	/// <summary>Writes <paramref name="text"/> to <c>api.http</c> in <paramref name="dir"/> (a new temp folder by default) and loads it.</summary>
	private static HttpFile Write(string text, string? dir = null)
	{
		dir ??= TempDir.Create();
		var path = Path.Combine(dir, "api.http");
		File.WriteAllText(path, text);
		return HttpFile.Load(path);
	}

	/// <summary>A client like a caller's: BaseAddress set, no redirects, no decompression.</summary>
	private static HttpClient Client(TestServer server) =>
		new(new SocketsHttpHandler { AllowAutoRedirect = false }) { BaseAddress = server.BaseAddress };

	private static HttpFileRunOptions Host(TestServer server, HttpFileRunOptions? options = null)
	{
		var variables = new Dictionary<string, string>(options?.Variables ?? new Dictionary<string, string>()) { ["host"] = server.BaseAddress.ToString().TrimEnd('/') };
		return new HttpFileRunOptions
		{
			Variables = variables,
			Secrets = options?.Secrets,
			Environment = options?.Environment,
			UserSecretsId = options?.UserSecretsId,
			UserSecretsRoot = options?.UserSecretsRoot,
			TimeProvider = options?.TimeProvider ?? TimeProvider.System,
			Random = options?.Random,
		};
	}

	private static void ShouldSucceed(HttpFileRunResult result) =>
		result.Failure.Should().BeNull(result.Failure?.ToString());

	[Test]
	public async Task ChainsALoginTokenIntoTheNextRequest()
	{
		await using var server = new TestServer();
		var file = Write("""
			# @name login
			POST {{host}}/login
			Content-Type: application/json

			{"user": "ada"}

			###
			GET {{host}}/secure
			Authorization: Bearer {{login.response.body.$.token}}

			###
			GET {{host}}/echo?q={{login.response.body.$.user.roles[1]}}
			X-Echo: {{login.response.body.$["user"].name}} {{login.request.body.$.user}} {{login.response.headers.content-type}} {{login.request.headers.Content-Type}}
			""");
		var result = await HttpFileRunner.RunAsync(file, options: Host(server));
		ShouldSucceed(result);
		result.Exchanges.Select(e => e.Response.StatusCode).Should().Equal(200, 200, 200);
		result.Exchanges[1].Sent.Headers.Should().Contain(new HttpHeaderField("Authorization", "Bearer " + TestServer.Token));
		result.Exchanges[2].Response.GetHeaderValues("X-Echo-Back").Single().Should().Be("ada ada application/json; charset=utf-8 application/json");
		result.Exchanges[2].Sent.Uri.Query.Should().Be("?q=dev");
	}

	[Test]
	public async Task ResolvesWholeBodyReferences()
	{
		await using var server = new TestServer();
		var file = Write("""
			# @name first
			GET {{host}}/target
			###
			GET {{host}}/echo
			X-Echo: {{first.response.body.*}}
			""");
		var result = await HttpFileRunner.RunAsync(file, options: Host(server));
		ShouldSucceed(result);
		result.Exchanges[1].Response.GetHeaderValues("X-Echo-Back").Single().Should().Be("target reached");
	}

	[Test]
	public async Task EnvironmentFilesLayerSharedNamedAndUserValues()
	{
		await using var server = new TestServer();
		var root = TempDir.Create();
		var dir = Path.Combine(root, "requests", "deeper");
		Directory.CreateDirectory(dir);
		File.WriteAllText(Path.Combine(root, "http-client.env.json"), $$"""
			{
			  "$shared": { "host": "{{server.BaseAddress.ToString().TrimEnd('/')}}", "fromShared": "shared", "overriddenByDev": "shared" },
			  "dev": { "overriddenByDev": "dev", "overriddenByUser": "dev", "beatenByFile": "env" },
			  "prod": { "overriddenByDev": "prod" }
			}
			""");
		File.WriteAllText(Path.Combine(root, "http-client.env.json.user"), /*lang=json,strict*/ """
			{ "dev": { "overriddenByUser": "user-value" } }
			""");
		var file = Write("""
			@beatenByFile = file
			GET {{host}}/echo
			X-Echo: {{fromShared}}|{{overriddenByDev}}|{{overriddenByUser}}|{{beatenByFile}}
			""", dir);

		var result = await HttpFileRunner.RunAsync(file, options: new HttpFileRunOptions { Environment = "dev" });
		ShouldSucceed(result);
		result.Exchanges[0].Response.GetHeaderValues("X-Echo-Back").Single().Should().Be("shared|dev|user-value|file");
		// A value from the .user file is sensitive: the run records it for redaction.
		result.SensitiveValues.Select(v => v.ToString()).Should().Equal("{{overriddenByUser}}");
		result.Redact("x user-value y").Should().Be("x {{overriddenByUser}} y");

		var env = HttpEnvironment.Load(file.Path, "prod");
		env.Names.Should().Equal("dev", "prod");
		env.Variables["overriddenByDev"].Value.Should().Be("prod");
		env.Variables["overriddenByDev"].Line.Should().Be(4);
		env.Variables.Should().NotContainKey("overriddenByUser");
	}

	[Test]
	public async Task OverrideVariablesBeatFileVariables()
	{
		await using var server = new TestServer();
		var file = Write("""
			@host = http://localhost:1
			GET {{host}}/echo
			""");
		var result = await HttpFileRunner.RunAsync(file, options: Host(server));
		ShouldSucceed(result);
	}

	[Test]
	public void AnUnknownEnvironmentIsAnError()
	{
		var dir = TempDir.Create();
		File.WriteAllText(Path.Combine(dir, "http-client.env.json"), /*lang=json,strict*/ """{ "dev": {}, "test": {} }""");
		var act = () => HttpEnvironment.Load(Path.Combine(dir, "api.http"), "prod");
		act.Should().Throw<HttpFileParseException>().Which.Message
			.Should().Be($"{Path.Combine(dir, "http-client.env.json")}:1: environment 'prod' is not defined; defined: dev, test");
	}

	[Test]
	public void EnvironmentFileErrorsHaveLines()
	{
		var dir = TempDir.Create();
		var path = Path.Combine(dir, "http-client.env.json");
		File.WriteAllText(path, /*lang=json,strict*/ "{\n  \"dev\": {\n    \"ok\": \"1\",\n    \"bad\": [1, 2]\n  },\n  \"other\": 5\n}");
		var act = () => HttpEnvironment.LoadFrom(dir, "dev");
		act.Should().Throw<HttpFileParseException>().Which.Errors.Select(e => e.ToString()).Should().Equal(
			$"{path}:4: 'bad' in environment 'dev' must be a string, a number, a boolean or a secret ({{\"provider\": ..., \"secretName\": ...}})",
			$"{path}:6: environment 'other' must be an object of variables");

		File.WriteAllText(path, "{\n  \"dev\": {\n    \"ok\": \n}");
		act.Should().Throw<HttpFileParseException>().Which.Errors.Single().Line.Should().Be(4);
	}

	[Test]
	public async Task FollowsRedirectsUnlessToldNotTo()
	{
		await using var server = new TestServer();
		var file = Write("""
			GET {{host}}/redirect
			###
			# @no-redirect
			GET {{host}}/redirect
			###
			POST {{host}}/redirect-303
			Content-Type: text/plain

			hello
			""");
		var result = await HttpFileRunner.RunAsync(file, Client(server), Host(server));
		ShouldSucceed(result);
		result.Exchanges[0].Response.StatusCode.Should().Be(200);
		result.Exchanges[0].Response.BodyText.Should().Be("target reached");
		result.Exchanges[0].Redirects.Should().Equal(new HttpRedirect(302, new Uri(server.BaseAddress, "/target")));
		result.Exchanges[1].Response.StatusCode.Should().Be(302);
		result.Exchanges[1].Response.GetHeaderValues("Location").Single().Should().EndWith("/target");
		result.Exchanges[1].Redirects.Should().BeEmpty();
		// 303 turns the POST into a GET without a body.
		result.Exchanges[2].Response.BodyText.Should().Contain("\"method\":\"GET\"").And.Contain("\"body\":\"\"");
		result.Exchanges[2].Sent.Method.Should().Be("POST");
	}

	[Test]
	public async Task StopsAfterTooManyRedirects()
	{
		await using var server = new TestServer();
		var file = Write("GET {{host}}/loop");
		var options = Host(server);
		var result = await HttpFileRunner.RunAsync(file, Client(server), new HttpFileRunOptions { Variables = options.Variables, MaxRedirects = 3 });
		result.Failure!.Message.Should().StartWith("stopped after 3 redirects");
		result.Exchanges.Should().BeEmpty();
	}

	[Test]
	public async Task RelativeUrlsResolveAgainstTheBaseAddress()
	{
		await using var server = new TestServer();
		var file = Write("GET /echo?q=relative");
		var result = await HttpFileRunner.RunAsync(file, Client(server));
		ShouldSucceed(result);
		result.Exchanges[0].Sent.Uri.Should().Be(new Uri(server.BaseAddress, "/echo?q=relative"));
		result.Exchanges[0].Response.BodyText.Should().Contain("\"q\":\"relative\"");

		var withoutBase = await HttpFileRunner.RunAsync(file);
		withoutBase.Failure!.ToString().Should().Be($"{file.Path}:1: '/echo?q=relative' is relative, and the HttpClient has no BaseAddress to resolve it against");
	}

	[Test]
	public void UrlsWithoutASchemeGetHttp()
	{
		using var client = new HttpClient();
		HttpFileRunner.ResolveUri("example.com/a", client, 1).Should().Be(new Uri("http://example.com/a"));
		HttpFileRunner.ResolveUri("localhost:5000/a", client, 1).Should().Be(new Uri("http://localhost:5000/a"));
		HttpFileRunner.ResolveUri("https://example.com/a", client, 1).Should().Be(new Uri("https://example.com/a"));
	}

	[Test]
	public async Task AConnectionFailureStopsTheRunAndKeepsEarlierExchanges()
	{
		await using var server = new TestServer();
		var file = Write($$$"""
			GET {{host}}/target
			###
			GET http://localhost:{{{TestServer.RefusedPort}}}/nothing
			###
			GET {{host}}/target
			""");
		var result = await HttpFileRunner.RunAsync(file, options: Host(server));
		result.Succeeded.Should().BeFalse();
		result.Exchanges.Should().ContainSingle();
		result.Failure!.Line.Should().Be(3);
		result.Failure.Request.Index.Should().Be(1);
		result.Failure.Exception.Should().BeOfType<HttpRequestException>();
		result.Failure.Message.Should().Contain("refused");
	}

	[Test]
	public async Task TheRefusedPortRefusesConnectionsQuickly()
	{
		using var client = new System.Net.Sockets.TcpClient();
		var started = System.Diagnostics.Stopwatch.StartNew();

		var thrown = await FluentActions.Awaiting(() => client.ConnectAsync(System.Net.IPAddress.Loopback, TestServer.RefusedPort)).Should().ThrowAsync<System.Net.Sockets.SocketException>();

		thrown.Which.SocketErrorCode.Should().Be(System.Net.Sockets.SocketError.ConnectionRefused);
		started.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3), "a refusal is immediate; a dropped connection is not");
	}

	[Test]
	public async Task ServersStartedTogetherEachGetTheirOwnPort()
	{
		// Many servers at once, as parallel tests start them: each must bind a port of its own.
		var servers = await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => Task.Run(() => new TestServer())));
		try
		{
			servers.Select(s => s.Port).Distinct().Should().HaveCount(40);
		}
		finally
		{
			foreach (var server in servers)
				await server.DisposeAsync();
		}
	}

	[Test]
	public async Task NotFoundIsAResponseNotAFailure()
	{
		await using var server = new TestServer();
		var result = await HttpFileRunner.RunAsync(Write("GET {{host}}/missing"), options: Host(server));
		ShouldSucceed(result);
		result.Exchanges[0].Response.StatusCode.Should().Be(404);
		result.Exchanges[0].Response.BodyText.Should().Be("not found");
	}

	[Test]
	public async Task ATimeoutIsAFailure()
	{
		await using var server = new TestServer();
		var result = await HttpFileRunner.RunAsync(Write("# @timeout 200 ms\nGET {{host}}/slow"), options: Host(server));
		result.Failure!.Message.Should().Be("timed out (the @timeout of 0.2s)");
	}

	[Test]
	public async Task DecodesCompressedBodiesTheClientLeftEncoded()
	{
		await using var server = new TestServer();
		var file = Write("GET {{host}}/gzip\n###\nGET {{host}}/brotli");
		var result = await HttpFileRunner.RunAsync(file, Client(server), Host(server));
		ShouldSucceed(result);
		result.Exchanges.Select(e => (e.Response.BodyText, e.Response.ContentEncoding)).Should().Equal(("zipped hello", "gzip"), ("brotli hello", "br"));

		// The runner's own client decompresses on the way in, so nothing is left to decode.
		var own = await HttpFileRunner.RunAsync(file, options: Host(server));
		ShouldSucceed(own);
		own.Exchanges.Select(e => (e.Response.BodyText, e.Response.ContentEncoding)).Should().Equal(("zipped hello", null), ("brotli hello", null));
	}

	[Test]
	public void DecodesDeflateWithAndWithoutTheZlibWrapper()
	{
		var text = Encoding.UTF8.GetBytes("deflated");
		using var zlib = new MemoryStream();
		using (var z = new System.IO.Compression.ZLibStream(zlib, System.IO.Compression.CompressionLevel.Fastest))
			z.Write(text);
		using var raw = new MemoryStream();
		using (var d = new System.IO.Compression.DeflateStream(raw, System.IO.Compression.CompressionLevel.Fastest))
			d.Write(text);
		HttpFileRunner.Decode(zlib.ToArray(), ["deflate"]).Body.Should().Equal(text);
		HttpFileRunner.Decode(raw.ToArray(), ["deflate"]).Body.Should().Equal(text);
		HttpFileRunner.Decode(text, ["identity"]).Should().Be((text, null));
		HttpFileRunner.Decode(text, ["zstd"]).Encoding.Should().BeNull();
	}

	[Test]
	public async Task ResolvesAspNetUserSecretsFromSecretsJson()
	{
		await using var server = new TestServer();
		var secretsRoot = TempDir.Create();
		Directory.CreateDirectory(Path.Combine(secretsRoot, "app-1234"));
		File.WriteAllText(Path.Combine(secretsRoot, "app-1234", "secrets.json"), /*lang=json,strict*/ """{ "Shop": { "ApiKey": "shop-secret-key" }, "Flat:Key": "flat-secret" }""");
		var dir = TempDir.Create();
		File.WriteAllText(Path.Combine(dir, "http-client.env.json"), /*lang=json,strict*/ """
			{ "dev": {
			  "ApiKey": { "provider": "AspnetUserSecrets", "secretName": "Shop:ApiKey" },
			  "Flat": { "provider": "AspnetUserSecrets", "secretName": "flat:key" }
			} }
			""");
		var file = Write("GET {{host}}/echo?q={{Flat}}\nX-Echo: {{ApiKey}}", dir);

		var result = await HttpFileRunner.RunAsync(file, options: Host(server, new HttpFileRunOptions { Environment = "dev", UserSecretsId = "app-1234", UserSecretsRoot = secretsRoot }));
		ShouldSucceed(result);
		result.Exchanges[0].Response.GetHeaderValues("X-Echo-Back").Single().Should().Be("shop-secret-key");
		result.Exchanges[0].Sent.Uri.Query.Should().Be("?q=flat-secret");

		// Without an id in the options, the nearest project file's <UserSecretsId> is used.
		File.WriteAllText(Path.Combine(dir, "App.csproj"), "<Project><PropertyGroup><UserSecretsId>app-1234</UserSecretsId></PropertyGroup></Project>");
		var fromProject = await HttpFileRunner.RunAsync(file, options: Host(server, new HttpFileRunOptions { Environment = "dev", UserSecretsRoot = secretsRoot }));
		ShouldSucceed(fromProject);

		// options.Secrets is asked first.
		var fromOptions = await HttpFileRunner.RunAsync(file, options: Host(server, new HttpFileRunOptions
		{
			Environment = "dev",
			UserSecretsRoot = secretsRoot,
			Secrets = name => name == "Shop:ApiKey" ? "from-options" : null,
		}));
		fromOptions.Exchanges[0].Response.GetHeaderValues("X-Echo-Back").Single().Should().Be("from-options");
	}

	[Test]
	public void FindsTheUserSecretsIdOnAnAssemblyByName()
	{
		UserSecrets.FromAssembly(typeof(RunnerTests).Assembly).Should().Be("test-assembly-secrets-id");
		UserSecrets.FromAssembly(typeof(HttpFile).Assembly).Should().BeNull();
	}

	[Test]
	public async Task OtherProvidersAndSecretReferencesNeedTheSecretsCallback()
	{
		await using var server = new TestServer();
		var dir = TempDir.Create();
		File.WriteAllText(Path.Combine(dir, "http-client.env.json"), /*lang=json,strict*/ """
			{ "dev": { "Vault": { "provider": "AzureKeyVault", "secretName": "VaultSecret", "resourceId": "/subscriptions/x" } } }
			""");
		var file = Write("GET {{host}}/echo\nX-Echo: {{Vault}} {{$secret Other}}", dir);

		var missing = await HttpFileRunner.RunAsync(file, options: Host(server, new HttpFileRunOptions { Environment = "dev" }));
		missing.Failure!.ToString().Should().Be(
			$"{file.Path}:2: secret 'VaultSecret' for '{{{{Vault}}}}' (provider AzureKeyVault, {Path.Combine(dir, "http-client.env.json")}:1) has no value: HttpFileRunOptions.Secrets did not answer it");

		var answered = await HttpFileRunner.RunAsync(file, options: Host(server, new HttpFileRunOptions
		{
			Environment = "dev",
			Secrets = name => name switch { "VaultSecret" => "vault-value", "Other" => "other-value", _ => null },
		}));
		ShouldSucceed(answered);
		answered.Exchanges[0].Response.GetHeaderValues("X-Echo-Back").Single().Should().Be("vault-value other-value");

		var noSecret = await HttpFileRunner.RunAsync(Write("GET {{host}}/echo\nX-Echo: {{$secret Nope}}"), options: Host(server));
		noSecret.Failure!.Message.Should().Be("'{{$secret Nope}}' has no value: HttpFileRunOptions.Secrets did not answer 'Nope'");
	}

	[Test]
	public async Task RedactsSecretsAndEnvironmentValuesEverywhere()
	{
		await using var server = new TestServer();
		var variable = "HTTPFILE_TEST_" + Guid.NewGuid().ToString("N");
		Environment.SetEnvironmentVariable(variable, "env-value-123");
		try
		{
			var file = Write($$$"""
				GET {{host}}/echo?q={{$processEnv {{{variable}}}}}
				X-Echo: {{$secret ApiKey}}
				""");
			var result = await HttpFileRunner.RunAsync(file, options: Host(server, new HttpFileRunOptions { Secrets = n => n == "ApiKey" ? "secret-api-key" : null }));
			ShouldSucceed(result);
			var raw = result.Exchanges[0];
			raw.Response.BodyText.Should().Contain("secret-api-key").And.Contain("env-value-123");

			var redacted = result.Redact(raw);
			redacted.Response.BodyText.Should().Contain("\"header\":\"{{$secret ApiKey}}\"").And.Contain($"\"q\":\"{{{{$processEnv {variable}}}}}\"");
			redacted.Response.BodyText.Should().NotContain("secret-api-key").And.NotContain("env-value-123");
			redacted.Response.GetHeaderValues("X-Echo-Back").Single().Should().Be("{{$secret ApiKey}}");
			redacted.Sent.Headers.Should().Contain(new HttpHeaderField("X-Echo", "{{$secret ApiKey}}"));
			redacted.Sent.Uri.OriginalString.Should().NotContain("env-value-123");
			result.SensitiveValues.Select(v => v.Reference).Should().Equal($"{{{{$processEnv {variable}}}}}", "{{$secret ApiKey}}");
		}
		finally
		{
			Environment.SetEnvironmentVariable(variable, null);
		}
	}

	[Test]
	public void RedactsTheLongestValueFirst()
	{
		var values = HttpFileRunResult.SortForRedaction([new HttpSensitiveValue("abcd", "{{short}}"), new HttpSensitiveValue("abcdefgh", "{{long}}")]);
		HttpFileRunResult.Redact("x abcdefgh y abcd", values).Should().Be("x {{long}} y {{short}}");
	}

	[Test]
	public async Task RedactsFailureMessages()
	{
		var file = Write("GET http://{{$secret Host}}:1/x");
		var result = await HttpFileRunner.RunAsync(file, options: new HttpFileRunOptions { Secrets = _ => "secret-host-name.invalid" });
		result.Failure.Should().NotBeNull();
		result.Failure.Message.Should().NotContain("secret-host-name").And.Contain("{{$secret Host}}");
	}

	[Test]
	public async Task RefusesSensitiveValuesTooShortToRedact()
	{
		var file = Write("GET http://localhost:1/x\nX-Key: {{$secret Short}}");
		var result = await HttpFileRunner.RunAsync(file, options: new HttpFileRunOptions { Secrets = _ => "abc" });
		result.Failure!.ToString().Should().Be($"{file.Path}:2: the value of '{{{{$secret Short}}}}' is shorter than 4 characters, too short to redact safely from the output");
	}

	[Test]
	public async Task ReadsDotEnvFilesUpToTheProjectFolder()
	{
		await using var server = new TestServer();
		var project = TempDir.Create();
		File.WriteAllText(Path.Combine(project, "App.csproj"), "<Project />");
		File.WriteAllText(Path.Combine(project, ".env"), "# comment\nexport FROM_PROJECT=\"project-value\"\nNEAR=far-value\n");
		var dir = Path.Combine(project, "http");
		Directory.CreateDirectory(dir);
		File.WriteAllText(Path.Combine(dir, ".env"), "NEAR=near-value\n");
		var file = Write("GET {{host}}/echo\nX-Echo: {{$dotenv FROM_PROJECT}} {{$dotenv NEAR}}", dir);
		var result = await HttpFileRunner.RunAsync(file, options: Host(server));
		ShouldSucceed(result);
		result.Exchanges[0].Response.GetHeaderValues("X-Echo-Back").Single().Should().Be("project-value near-value");
		result.Redact("near-value").Should().Be("{{$dotenv NEAR}}");
	}

	[Test]
	public async Task GeneratesRepeatableDynamicValues()
	{
		await using var server = new TestServer();
		var file = Write("""
			GET {{host}}/echo
			X-Echo: {{$guid}}|{{$randomInt 5 6}}|{{$datetime iso8601}}|{{$datetime rfc1123 1 d}}|{{$datetime "dd-MM-yyyy" -1 y}}|{{$timestamp}}|{{$isoTimestamp}}|{{$localDatetime "HH:mm zzz"}}|{{$timestamp -1 h}}|{{$datetime iso8601 2 M}}|{{$datetime iso8601 -1500 ms}}
			""");
		var time = new FixedTime(new DateTimeOffset(2024, 1, 17, 22, 59, 55, TimeSpan.Zero));
		async Task<string[]> Run(int seed)
		{
			var result = await HttpFileRunner.RunAsync(file, options: Host(server, new HttpFileRunOptions { Random = new Random(seed), TimeProvider = time }));
			ShouldSucceed(result);
			return result.Exchanges[0].Response.GetHeaderValues("X-Echo-Back").Single().Split('|');
		}
		var first = await Run(42);
		var again = await Run(42);
		var other = await Run(7);
		again.Should().Equal(first);
		other[0].Should().NotBe(first[0]);
		Guid.Parse(first[0]).Version.Should().Be(4);
		first[1..].Should().Equal(
			"5",
			"2024-01-17T22:59:55.0000000+00:00",
			"Thu, 18 Jan 2024 22:59:55 GMT",
			"17-01-2023",
			"1705532395",
			"2024-01-17T22:59:55.000Z",
			"14:59 -08:00",
			"1705528795",
			"2024-03-17T22:59:55.0000000+00:00",
			"2024-01-17T22:59:53.5000000+00:00");
	}

	[Test]
	public async Task ReportsUnresolvableReferencesWithTheirLine()
	{
		await using var server = new TestServer();
		var unknown = await HttpFileRunner.RunAsync(Write("GET {{host}}/echo\n\n{\n  \"a\": \"{{missing}}\"\n}"), options: Host(server));
		unknown.Failure!.Line.Should().Be(4);
		unknown.Failure.Message.Should().Be("unknown variable 'missing' (no http-client.env.json was found)");

		var notRun = await HttpFileRunner.RunAsync(Write("GET {{host}}/echo\nX-Echo: {{later.response.body.*}}\n###\n# @name later\nGET {{host}}/echo"), options: Host(server));
		notRun.Failure!.Message.Should().Be("'{{later.response.body.*}}' references request 'later' (line 5), which has not run yet");

		var noMatch = await HttpFileRunner.RunAsync(Write("# @name login\nPOST {{host}}/login\n###\nGET {{host}}/echo\nX-Echo: {{login.response.body.$.user.roles[5]}}"), options: Host(server));
		noMatch.Failure!.ToString().Should().EndWith(":5: '{{login.response.body.$.user.roles[5]}}': '$.user.roles[5]' matches nothing in the response body of 'login'");
		noMatch.Exchanges.Should().ContainSingle();

		var notJson = await HttpFileRunner.RunAsync(Write("# @name t\nGET {{host}}/target\n###\nGET {{host}}/echo\nX-Echo: {{t.response.body.$.a}}"), options: Host(server));
		notJson.Failure!.Message.Should().Be("'{{t.response.body.$.a}}': the response body of 't' is not JSON");
	}

	[Test]
	public async Task SendsFileBodiesAndFormBodies()
	{
		await using var server = new TestServer();
		var dir = TempDir.Create();
		File.WriteAllText(Path.Combine(dir, "raw.txt"), "raw {{not substituted}}");
		File.WriteAllText(Path.Combine(dir, "template.txt"), "hello {{who}}");
		var file = Write("""
			@who = world
			POST {{host}}/echo
			Content-Type: text/plain

			< ./raw.txt
			###
			POST {{host}}/echo
			Content-Type: text/plain

			<@ ./template.txt
			###
			POST {{host}}/echo
			Content-Type: application/x-www-form-urlencoded

			name=foo
			&password=bar
			""", dir);
		var result = await HttpFileRunner.RunAsync(file, options: Host(server));
		ShouldSucceed(result);
		result.Exchanges.Select(e => e.Sent.BodyText).Should().Equal("raw {{not substituted}}", "hello world", "name=foo&password=bar");
		result.Exchanges[0].Response.BodyText.Should().Contain("\"contentType\":\"text/plain\"");
	}

	[Test]
	public async Task KeepsCookiesInItsOwnClientUnlessToldNotTo()
	{
		await using var server = new TestServer();
		var file = Write("""
			GET {{host}}/set-cookie
			###
			GET {{host}}/echo
			###
			# @no-cookie-jar
			GET {{host}}/echo
			""");
		var result = await HttpFileRunner.RunAsync(file, options: Host(server));
		ShouldSucceed(result);
		result.Exchanges[1].Response.BodyText.Should().Contain("\"cookie\":\"session=abc123\"");
		result.Exchanges[1].Sent.Headers.Should().Contain(new HttpHeaderField("Cookie", "session=abc123"));
		result.Exchanges[2].Response.BodyText.Should().Contain("\"cookie\":\"\"");
	}

	[Test]
	public async Task CancellationThrows()
	{
		await using var server = new TestServer();
		using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
		var act = () => HttpFileRunner.RunAsync(Write("GET {{host}}/slow"), options: Host(server), cancellationToken: cts.Token);
		await act.Should().ThrowAsync<OperationCanceledException>();
	}

	private sealed class FixedTime(DateTimeOffset now) : TimeProvider
	{
		public override DateTimeOffset GetUtcNow() => now;

		public override TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.CreateCustomTimeZone("Test-8", TimeSpan.FromHours(-8), "Test-8", "Test-8");
	}
}
