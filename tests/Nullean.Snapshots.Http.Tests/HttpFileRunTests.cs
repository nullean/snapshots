using System.IO.Abstractions.TestingHelpers;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Nullean.HttpFileParser;
using Nullean.Snapshots.Tests;

namespace Nullean.Snapshots.Http.Tests;

/// <summary><c>.http</c> files run against a real server, in folders of their own.</summary>
public class HttpFileRunTests
{
	private static TestServer Server => TestServer.Shared;

	private static Dictionary<string, string> Host() => new(StringComparer.Ordinal) { ["host"] = Server.Host };

	/// <summary>Writes the files into a fresh folder with no project file above it and returns the first one's path.</summary>
	private static string Files(params (string Name, string Content)[] files)
	{
		var dir = Path.Combine(Path.GetTempPath(), "snapshots-http-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		foreach (var (name, content) in files)
			File.WriteAllText(Path.Combine(dir, name), content);
		return Path.Combine(dir, files[0].Name);
	}

	private const string Chained = """
		### login
		POST {{host}}/api/login
		Content-Type: application/json

		{"user": "ada"}

		###
		GET {{host}}/api/orders
		Authorization: Bearer {{login.response.body.$.token}}

		###
		GET {{host}}/api/missing
		""";

	[Test]
	[Arguments(TranscriptMode.Compare)]
	[Arguments(TranscriptMode.Off)]
	public async Task AnUnmetExpectationStopsTheRunInBothTranscriptModes(TranscriptMode mode)
	{
		var file = Files(("shop.http", "# @expect status 200\nGET {{host}}/api/missing\n\n###\nGET {{host}}/api/text"));

		var transcript = await HttpFileSnapshot.RunAsync(file, new HttpFileSnapshotOptions { Variables = Host(), Transcript = mode });
		// Even a transcript that matches its verified file fails when a request stopped the run.
		var result = HttpFileSnapshot.WithFailure(transcript, new SnapshotResult(true, null, string.Empty, string.Empty, string.Empty));

		transcript.Failure!.Line.Should().Be(1);
		transcript.Failure.Message.Should().Be("@expect status 200 failed: the response was 404 Not Found");
		transcript.Text.Should().StartWith("## GET {{host}}/api/missing\nstatus: 404 Not Found", "the response that missed the expectation is in the transcript");
		result.Passed.Should().BeFalse();
		result.FailureMessage.Should().StartWith("Request stopped at ").And.Contain("GET {{host}}/api/missing\n@expect status 200 failed: the response was 404 Not Found");
	}

	[Test]
	public async Task AShapeRunWritesBodiesAsTheirShapeAndIsComparedLikeAnyTranscript()
	{
		var file = Files(("shape.http", Chained));

		var plain = await HttpFileSnapshot.RunAsync(file, new HttpFileSnapshotOptions { Variables = Host() });
		var shape = await HttpFileSnapshot.RunAsync(file, new HttpFileSnapshotOptions { Variables = Host(), Transcript = TranscriptMode.Shape });

		shape.Failure.Should().BeNull();
		shape.Mode.Should().Be(TranscriptMode.Compare, "the text is already the shape, so it is compared against the verified shape");
		shape.Text.Should().NotBe(plain.Text);
		shape.Text.Should().Contain("\"token\": \"string\"").And.NotContain(Server.Host);
		HttpSnapshot.Format.Compare(shape.Text, shape.Text).IsMatch.Should().BeTrue();
	}

	[Test]
	public async Task MetExpectationsLeaveTheTranscriptUnchanged()
	{
		var plain = Files(("a.http", "GET {{host}}/api/text"));
		var expecting = Files(("b.http", "# @expect status 200\n# @expect header content-type ~ text\nGET {{host}}/api/text"));

		var withoutExpectations = await HttpFileSnapshot.RunAsync(plain, new HttpFileSnapshotOptions { Variables = Host() });
		var withExpectations = await HttpFileSnapshot.RunAsync(expecting, new HttpFileSnapshotOptions { Variables = Host() });

		withExpectations.Failure.Should().BeNull();
		withExpectations.Text.Should().Be(withoutExpectations.Text);
	}

	[Test]
	public async Task AReadOnlyRunRefusesAPostUnlessItIsMarkedReadonly()
	{
		var refused = Files(("w.http", "GET {{host}}/api/text\n\n###\nPOST {{host}}/api/echo\n\nhi"));
		var allowed = Files(("r.http", "# @readonly\nPOST {{host}}/api/echo\n\nhi"));

		var blocked = await HttpFileSnapshot.RunAsync(refused, new HttpFileSnapshotOptions { Variables = Host(), ReadOnly = true });
		var passed = await HttpFileSnapshot.RunAsync(allowed, new HttpFileSnapshotOptions { Variables = Host(), ReadOnly = true });

		blocked.Failure!.Message.Should().StartWith("not sent: a read-only run sends only GET, HEAD and OPTIONS requests, and this is POST.");
		blocked.Text.Should().Contain("GET {{host}}/api/text").And.NotContain("POST");
		passed.Failure.Should().BeNull();
	}

	[Test]
	public async Task AChainedLoginPassesItsTokenOn()
	{
		var transcript = await HttpFileSnapshot.RunAsync(Files(("shop.http", Chained)), new HttpFileSnapshotOptions { Variables = Host() });

		transcript.Failure.Should().BeNull();
		transcript.Text.Should().Be($$$"""
			## login: POST {{host}}/api/login
			status: 200 OK
			content-type: application/json; charset=utf-8

			{
			  "token": "{{{TestServer.Token}}}",
			  "user": {
			    "name": "ada",
			    "roles": [
			      "admin",
			      "dev"
			    ]
			  }
			}

			## GET {{host}}/api/orders
			status: 200 OK
			content-type: application/json; charset=utf-8

			{
			  "orders": [
			    {
			      "id": 1,
			      "total": 12.5
			    },
			    {
			      "id": 2,
			      "total": 3
			    }
			  ]
			}

			## GET {{host}}/api/missing
			status: 404 Not Found
			content-type: text/plain; charset=utf-8

			> not found

			""".Replace("\r\n", "\n", StringComparison.Ordinal));
	}

	[Test]
	public async Task AUserEnvironmentFileOverridesAndIsRedacted()
	{
		var file = Files(
			("whoami.http", "GET {{host}}/api/whoami?user={{user}}\n"),
			(HttpEnvironment.FileName, $$"""{ "dev": { "host": "{{Server.Host}}", "user": "ada" } }"""),
			(HttpEnvironment.UserFileName, /*lang=json,strict*/ """{ "dev": { "user": "grace-hopper" } }"""));

		var transcript = await HttpFileSnapshot.RunAsync(file, new HttpFileSnapshotOptions { Environment = "dev" });

		transcript.Text.Should().Be("""
			## GET {{host}}/api/whoami?user={{user}}
			status: 200 OK
			content-type: application/json; charset=utf-8

			{
			  "user": "{{user}}"
			}

			""".Replace("\r\n", "\n", StringComparison.Ordinal), "the .user value reached the server and is redacted on the way back");
	}

	private const string EnvName = "SNAPSHOTS_HTTP_TEST_VALUE";
	private const string EnvValue = "env-value-271828";
	private const string Secret = "sk-live+Secret/42==";

	[Test]
	public async Task EchoedSecretsAndEnvironmentValuesNeverReachTheTranscript()
	{
		Environment.SetEnvironmentVariable(EnvName, EnvValue);
		var file = Files(("echo.http", $$$"""
			POST {{host}}/api/echo?k={{$secret ApiKey}}
			X-Api-Key: {{$secret ApiKey}}
			Content-Type: text/plain

			env={{$processEnv {{{EnvName}}}}}
			"""));

		var transcript = await HttpFileSnapshot.RunAsync(file, new HttpFileSnapshotOptions
		{
			Variables = Host(),
			Secrets = name => name == "ApiKey" ? Secret : null,
			Headers = ["X-Echo", "Set-Cookie"],
		});

		transcript.Text.Should().NotContain(Secret).And.NotContain(EnvValue).And.NotContain("sk-live");
		transcript.Text.Should().Be($$$"""
			## POST {{host}}/api/echo?k={{$secret ApiKey}}
			status: 200 OK
			content-type: application/json; charset=utf-8
			x-echo: {{$secret ApiKey}}

			{
			  "key": "{{$secret ApiKey}}",
			  "escaped": "{{$secret ApiKey}}",
			  "body": "env={{$processEnv {{{EnvName}}}}}"
			}

			""".Replace("\r\n", "\n", StringComparison.Ordinal));
	}

	[Test]
	public async Task AConnectionFailureStopsTheRunAndComesFirst()
	{
		var file = Files(("down.http", $"GET {{{{host}}}}/api/plain\n\n###\nGET http://localhost:{TestServer.RefusedPort}/never\n\n###\nGET {{{{host}}}}/api/plain\n"));

		var transcript = await HttpFileSnapshot.RunAsync(file, new HttpFileSnapshotOptions { Variables = Host() });

		transcript.Failure.Should().NotBeNull();
		transcript.Failure!.Line.Should().Be(4);
		transcript.Failure.Request.Should().StartWith("GET http://localhost:");
		transcript.Text.Should().StartWith("## GET {{host}}/api/plain\n").And.NotContain("never");
		HttpTranscript.Parse(transcript.Text).Items.Should().HaveCount(1, "the run stopped at the second request");

		var p = new TestProject();
		var inProject = transcript with { File = p.Fs.Path.Combine(p.Project.Directory, "http", "down.http") };
		p.Fs.AddFile(p.VerifiedFile("http/down", "responses"), new MockFileData(transcript.Text));
		var result = HttpFileSnapshot.WithFailure(inProject, p.Engine.MatchFile(inProject.Text, HttpFileSnapshot.Format, inProject.File, null, null));

		result.Passed.Should().BeFalse("a failed request fails even when the responses before it match");
		var lines = result.FailureMessage!.Split('\n');
		lines[0].Should().EndWith("down.http:4: " + transcript.Failure.Request).And.StartWith("Request stopped at ");
		lines[1].Should().Be(transcript.Failure.Message).And.NotBeEmpty();
	}

	[Test]
	public async Task AHostVariableAndARelativeUrlWithABaseAddressGiveTheSameTranscript()
	{
		var file = Files(("plain.http", "GET {{host}}/api/plain\n\n###\nGET {{host}}/api/missing\n"));

		var absolute = await HttpFileSnapshot.RunAsync(file, new HttpFileSnapshotOptions { Variables = Host() });
		using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { BaseAddress = Server.BaseAddress };
		var relative = await HttpFileSnapshot.RunAsync(file, new HttpFileSnapshotOptions
		{
			Client = client,
			Variables = new Dictionary<string, string>(StringComparer.Ordinal) { ["host"] = "" },
		});

		relative.Failure.Should().BeNull();
		relative.Text.Should().Be(absolute.Text).And.StartWith("## GET {{host}}/api/plain\nstatus: 200 OK\n");
	}

	[Test]
	public async Task AParseErrorThrows()
	{
		var file = Files(("bad.http", "GET {{host}}/a\n> {% client.global.set('x', 1) %}\n"));

		var act = () => HttpFileSnapshot.RunAsync(file);

		await act.Should().ThrowAsync<HttpFileParseException>();
	}

	[Test]
	public async Task RunsAgainstAnInMemoryAspNetCoreServer()
	{
		var builder = WebApplication.CreateSlimBuilder();
		builder.WebHost.UseTestServer();
		await using var app = builder.Build();
		app.MapPost("/api/login", () => Results.Json(new Dictionary<string, string> { ["token"] = "in-memory" }));
		app.MapGet("/api/orders", (HttpRequest r) => r.Headers.Authorization == "Bearer in-memory" ? Results.Text("two orders") : Results.Unauthorized());
		await app.StartAsync();

		var file = Files(("app.http", "### login\nPOST /api/login\n\n###\nGET /api/orders\nAuthorization: Bearer {{login.response.body.$.token}}\n"));
		var transcript = await HttpFileSnapshot.RunAsync(file, new HttpFileSnapshotOptions { Client = app.GetTestClient() });

		transcript.Failure.Should().BeNull();
		transcript.Text.Should().Be("""
			## login: POST /api/login
			status: 200 OK
			content-type: application/json; charset=utf-8

			{
			  "token": "in-memory"
			}

			## GET /api/orders
			status: 200 OK
			content-type: text/plain; charset=utf-8

			> two orders

			""".Replace("\r\n", "\n", StringComparison.Ordinal));
	}

	[Test]
	public void AFailedRequestComesBeforeTheDifference()
	{
		var p = new TestProject();
		var file = p.Fs.Path.Combine(p.Project.Directory, "http", "shop.http");
		p.Fs.AddFile(p.VerifiedFile("http/shop", "responses"), new MockFileData("## GET {{host}}/a\nstatus: 200 OK\n"));
		var transcript = new HttpTranscript(file, "## GET {{host}}/a\nstatus: 500 Internal Server Error\n",
			new HttpFileFailure(7, "GET {{host}}/b", "Connection refused (localhost:1)"));

		var result = HttpFileSnapshot.WithFailure(transcript, p.Engine.MatchFile(transcript.Text, HttpFileSnapshot.Format, transcript.File, null, null));

		result.FailureMessage!.Replace(Relative(file), "shop.http", StringComparison.Ordinal).Should().MatchSnapshot();
	}

	private static string Relative(string file)
	{
		var relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), file);
		return (relative.StartsWith("..", StringComparison.Ordinal) ? file : relative).Replace('\\', '/');
	}

	[Test]
	public void FindsTheProjectsHttpFiles() =>
		HttpFileSnapshot.Find().Should().Equal("http/content.http", "http/orders.http");

	[Test]
	public void HttpFilesYieldsHttpFilesOnly()
	{
		var attribute = new TUnit.HttpFilesAttribute();
		var assembly = typeof(HttpFileRunTests).Assembly;

		attribute.Pattern.Should().Be("*.http");
		attribute.Folder.Should().Be("http");
		attribute.Files(assembly, SnapshotFile.Find(assembly, attribute.Folder, attribute.Pattern)).Select(f => f.Name)
			.Should().Equal("content", "orders");
	}

	[Test]
	public async Task StatusCodesAreRecordedNotFailures()
	{
		var transcript = await HttpFileSnapshot.RunAsync(Files(("e.http", "GET {{host}}/api/orders\n")), new HttpFileSnapshotOptions { Variables = Host() });

		transcript.Failure.Should().BeNull();
		transcript.Text.Should().Contain("status: 401 Unauthorized\n");
	}
}
