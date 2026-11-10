using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using AwesomeAssertions;
using Nullean.HttpFileParser;
using Nullean.Snapshots.Cli;
using Nullean.Snapshots.Tests;

namespace Nullean.Snapshots.Http.Tests;

/// <summary>'snap http run' on a mock file system with a fake runner, and once for real against the test server.</summary>
public class HttpCliTests
{
	private const string Good = "## GET {{host}}/a\nstatus: 200 OK\n";
	private const string Changed = "## GET {{host}}/a\nstatus: 500 Internal Server Error\n";

	private sealed class Run
	{
		public TestProject P { get; } = new();
		public StringWriter Out { get; } = new();
		public StringWriter Err { get; } = new();
		public Dictionary<string, string?> Env { get; } = [];
		public List<(string File, HttpFileSnapshotOptions Options)> Ran { get; } = [];
		public Func<string, HttpTranscript>? Transcript { get; set; }

		public Run()
		{
			P.Fs.AddFile(P.Fs.Path.Combine(P.Project.Directory, "http", "shop.http"), new MockFileData("GET {{host}}/a\n"));
			P.Fs.AddFile(P.Fs.Path.Combine(P.Project.Directory, "http", "nested", "basket.http"), new MockFileData("GET {{host}}/b\n"));
			P.Fs.AddFile(P.Fs.Path.Combine(P.Project.Directory, "bin", "copy.http"), new MockFileData("GET {{host}}/a\n"));
			P.Fs.AddFile(P.Fs.Path.Combine(P.Project.Directory, ".hidden", "skip.http"), new MockFileData("GET {{host}}/a\n"));
			Transcript = file => new HttpTranscript(file, Good, null);
			// The project's tests have run before, so its received folder under the artifacts path exists.
			P.Fs.AddDirectory(P.Project.ReceivedDirectory);
		}

		public Task<int> Invoke(string[] patterns, string? environment = null, string[]? variables = null, string[]? headers = null, string? variant = null, string? transcript = null) =>
			new HttpCli(P.Fs, P.Project.Directory, Out, Err, (file, options, _) =>
			{
				Ran.Add((file, options));
				return Task.FromResult(Transcript!(file));
			}, k => Env.GetValueOrDefault(k)).RunAsync(patterns, environment, variables, CancellationToken.None, headers, variant, transcript);

		public string Output => Out.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);

		public string Error => Err.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
	}

	[Test]
	public void WithTheTranscriptOffNoVerifiedFileIsNeeded()
	{
		var run = new Run();
		var transcript = new HttpTranscript(run.P.Fs.Path.Combine(run.P.Project.Directory, "http", "shop.http"), Changed, null) { Mode = TranscriptMode.Off };

		HttpFileSnapshot.Match(transcript).Passed.Should().BeTrue();
		run.P.Fs.AllFiles.Should().NotContain(f => f.Contains("received", StringComparison.Ordinal));
	}

	[Test]
	public void WithTheTranscriptOffAFailedRequestStillFails()
	{
		var run = new Run();
		var failure = new HttpFileFailure(1, "GET {{host}}/a", "The connection was refused.");
		var transcript = new HttpTranscript(run.P.Fs.Path.Combine(run.P.Project.Directory, "http", "shop.http"), Good, failure) { Mode = TranscriptMode.Off };

		var result = HttpFileSnapshot.Match(transcript);

		result.Passed.Should().BeFalse();
		result.FailureMessage.Should().StartWith("Request stopped at ").And.EndWith("GET {{host}}/a\nThe connection was refused.");
	}

	[Test]
	public void MatchUsesTheTranscriptsVariant()
	{
		var run = new Run();
		var file = run.P.Fs.Path.Combine(run.P.Project.Directory, "http", "shop.http");
		run.P.Fs.AddFile(run.P.VerifiedFile("http/shop.edge", "responses"), new MockFileData(Good));
		Snapshot.EngineOverride.Value = run.P.Engine;
		try
		{
			var edge = HttpFileSnapshot.Match(new HttpTranscript(file, Good, null) { Variant = "edge" });
			var plain = HttpFileSnapshot.Match(new HttpTranscript(file, Good, null));

			edge.Passed.Should().BeTrue();
			plain.Passed.Should().BeFalse("the plain file has no verified snapshot");
		}
		finally
		{
			Snapshot.EngineOverride.Value = null;
		}
	}

	[Test]
	public async Task TheVariantFlagComparesWithTheVariantFile()
	{
		var run = new Run();
		run.P.Fs.AddFile(run.P.VerifiedFile("http/shop.edge", "responses"), new MockFileData(Good));

		(await run.Invoke(["http/shop.http"], variant: "edge")).Should().Be(0);

		run.Output.Should().Be("ok   Acme.Tests/http/shop.edge\n");
		run.Ran.Should().ContainSingle().Which.Options.Variant.Should().Be("edge");
	}

	[Test]
	public async Task TheTranscriptFlagOffNeedsNoVerifiedFileAndStillReportsAFailedRequest()
	{
		var run = new Run();
		run.Transcript = file => file.EndsWith("basket.http", StringComparison.Ordinal)
			? new HttpTranscript(file, Good, new HttpFileFailure(1, "GET {{host}}/b", "The connection was refused."))
			: new HttpTranscript(file, Changed, null);

		(await run.Invoke(["http/**/*.http"], transcript: "off")).Should().Be(1);

		run.Output.Should().StartWith("Request stopped at ");
		run.Output.Should().Contain("ok   http/shop.http (transcript off)");
		run.Ran.Should().AllSatisfy(r => r.Options.Transcript.Should().Be(TranscriptMode.Off));
		run.P.Fs.AllFiles.Should().NotContain(f => f.Contains("received", StringComparison.Ordinal));
	}

	[Test]
	public async Task ABadTranscriptOrVariantFlagExitsTwo()
	{
		var run = new Run();

		(await run.Invoke(["http/shop.http"], transcript: "maybe")).Should().Be(2);
		(await run.Invoke(["http/shop.http"], variant: "no/slash")).Should().Be(2);

		run.Error.Should().Be(
			"Error: --transcript 'maybe' is not 'compare' or 'off'.\n" +
			"Error: --variant 'no/slash' must be a name of letters, digits, '.', '-' and '_', without a leading or trailing dot.\n");
		run.Ran.Should().BeEmpty();
	}

	[Test]
	public async Task MatchingFilesPrintOkAndExitZero()
	{
		var run = new Run();
		run.P.Fs.AddFile(run.P.VerifiedFile("http/shop", "responses"), new MockFileData(Good));
		run.P.Fs.AddFile(run.P.VerifiedFile("http/nested/basket", "responses"), new MockFileData(Good));

		(await run.Invoke(["http/**/*.http"], "dev", ["host=http://localhost:5000", "token=a=b"], ["X-Request-Id"])).Should().Be(0);

		run.Output.Should().Be("ok   Acme.Tests/http/nested/basket\nok   Acme.Tests/http/shop\n");
		run.Ran.Should().AllSatisfy(r =>
		{
			r.Options.Environment.Should().Be("dev");
			r.Options.Client.Should().BeNull("the CLI uses the runner's own client");
			r.Options.Variables.Should().BeEquivalentTo(new Dictionary<string, string> { ["host"] = "http://localhost:5000", ["token"] = "a=b" });
			r.Options.Headers.Should().Equal("X-Request-Id");
		});
	}

	[Test]
	public async Task AGlobFromTheRootSkipsBuildOutputAndDotFolders()
	{
		var run = new Run();

		await run.Invoke(["**/*.http"]);

		run.Ran.Select(r => run.P.Fs.Path.GetRelativePath(run.P.Project.Directory, r.File).Replace('\\', '/'))
			.Should().Equal("http/nested/basket.http", "http/shop.http");
	}

	[Test]
	public async Task ADifferenceWritesReceivedAndExitsOneWithoutAManifest()
	{
		var run = new Run();
		run.P.Fs.AddFile(run.P.VerifiedFile("http/shop", "responses"), new MockFileData(Good));
		run.Transcript = file => new HttpTranscript(file, Changed, null);

		(await run.Invoke(["http/shop.http"])).Should().Be(1);

		run.Output.Should().StartWith("HTTP did not match snapshot: tests/Acme.Tests/.snapshots/http/shop.verified.responses\n");
		run.Output.Should().Contain("Accept:   snap accept Acme.Tests/http/shop");
		run.P.Read(run.P.ReceivedFile("http/shop", "responses")).Should().Be(Changed);
		run.P.Fs.File.Exists(run.P.Fs.Path.Combine(run.P.Project.ReceivedDirectory, RunManifest.FileName)).Should().BeFalse();
	}

	[Test]
	public async Task AFailedRequestExitsOneAndComesFirst()
	{
		var run = new Run();
		run.P.Fs.AddFile(run.P.VerifiedFile("http/shop", "responses"), new MockFileData(Good));
		run.Transcript = file => new HttpTranscript(file, Good, new HttpFileFailure(3, "GET {{host}}/b", "Connection refused"));

		(await run.Invoke(["http/shop.http"])).Should().Be(1);

		run.Output.Should().MatchRegex(@"^Request stopped at \S*shop\.http:3: GET \{\{host\}\}/b\nConnection refused\n");
	}

	[Test]
	public async Task NeverAccepts()
	{
		var run = new Run();
		run.Env["SNAPSHOT_ACCEPT"] = "1";

		(await run.Invoke(["http/shop.http"])).Should().Be(1);

		run.P.Fs.File.Exists(run.P.VerifiedFile("http/shop", "responses")).Should().BeFalse();
		run.Error.Should().StartWith("SNAPSHOT_ACCEPT is ignored: 'snap http run' never accepts.");
	}

	[Test]
	public async Task UsageErrorsExitTwo()
	{
		var run = new Run();
		(await run.Invoke([])).Should().Be(2);
		(await run.Invoke(["http/shop.http"], variables: ["novalue"])).Should().Be(2);
		(await run.Invoke(["http/shop.http"], variables: ["=x"])).Should().Be(2);
		(await run.Invoke(["http/missing.http", "http/*.nope"])).Should().Be(2);

		run.Error.Should().Be(
			"Error: name the .http files to run: files or globs such as 'http/**/*.http'.\n" +
			"Error: --var 'novalue' is not name=value.\n" +
			"Error: --var '=x' is not name=value.\n" +
			"Error: no .http file matches 'http/missing.http'.\n" +
			"Error: no .http file matches 'http/*.nope'.\n");
		run.Ran.Should().BeEmpty();
	}

	[Test]
	public async Task AParseErrorExitsTwoAndTheOthersStillRun()
	{
		var run = new Run();
		run.P.Fs.AddFile(run.P.VerifiedFile("http/shop", "responses"), new MockFileData(Good));
		run.Transcript = file => file.EndsWith("basket.http", StringComparison.Ordinal)
			? throw ParseError()
			: new HttpTranscript(file, Good, null);

		(await run.Invoke(["http/**/*.http"])).Should().Be(2);

		run.Error.Should().StartWith("basket.http:2: ");
		run.Output.Should().Contain("ok   Acme.Tests/http/shop");
	}

	[Test]
	public async Task AnUnreadableFileExitsThreeAndTheOthersStillRun()
	{
		var run = new Run();
		run.P.Fs.AddFile(run.P.VerifiedFile("http/shop", "responses"), new MockFileData(Good));
		run.Transcript = file => file.EndsWith("basket.http", StringComparison.Ordinal)
			? throw new IOException("The process cannot access the file.")
			: new HttpTranscript(file, Good, null);

		(await run.Invoke(["http/**/*.http"])).Should().Be(3);

		run.Error.Should().Be("Error: http/nested/basket.http: The process cannot access the file.\n");
		run.Output.Should().Contain("ok   Acme.Tests/http/shop");
	}

	[Test]
	public async Task RunsTheProjectsFilesForReal()
	{
		var output = new StringWriter();
		var error = new StringWriter();
		var project = HttpFileSnapshot.ProjectDirectory(ThisFile());
		var cli = new HttpCli(new FileSystem(), project, output, error, HttpFileSnapshot.RunFileAsync, _ => null);

		var exit = await cli.RunAsync(["http/orders.http"], null, ["host=" + TestServer.Shared.Host], CancellationToken.None);

		(output + error.ToString()).Should().Be("ok   Nullean.Snapshots.Http.Tests/http/orders" + Environment.NewLine);
		exit.Should().Be(0);
	}

	private static HttpFileParseException ParseError()
	{
		try
		{
			_ = HttpFile.Parse("GET /a\n> {% client.global.set('x', 1) %}\n", "basket.http");
		}
		catch (HttpFileParseException e)
		{
			return e;
		}

		throw new InvalidOperationException("The script should not parse.");
	}

	private static string ThisFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
}
