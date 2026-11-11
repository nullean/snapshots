using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Nullean.HttpFileParser;
using Nullean.Snapshots.Trees;

namespace Nullean.Snapshots.Http;

/// <summary>How <see cref="HttpFileSnapshot.RunAsync(string, HttpFileSnapshotOptions?, CancellationToken, string)"/> runs a <c>.http</c> file.</summary>
public sealed class HttpFileSnapshotOptions
{
	/// <summary>
	/// The client to send with, such as <c>WebApplicationFactory.CreateClient()</c>: URLs starting with <c>/</c> resolve
	/// against its <see cref="HttpClient.BaseAddress"/>. <see langword="null"/> uses the runner's own client, which
	/// follows no redirects itself, keeps a cookie jar per run and decompresses bodies. The runner follows redirects, so
	/// a client of your own should not (<c>AllowAutoRedirect = false</c>).
	/// </summary>
	public HttpClient? Client { get; init; }

	/// <summary>The environment to select from <c>http-client.env.json</c>; <see langword="null"/> uses only <c>$shared</c>.</summary>
	public string? Environment { get; init; }

	/// <summary>Variables that beat file variables and the environment, like <c>--var host=...</c> on <c>snap http run</c>.</summary>
	public IReadOnlyDictionary<string, string> Variables { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

	/// <summary>
	/// Answers secrets by name, for <c>{{$secret Name}}</c> and environment secrets, before user secrets are read:
	/// <c>Secrets = name =&gt; configuration[name]</c>. Every value it gives is redacted from the transcript.
	/// </summary>
	public Func<string, string?>? Secrets { get; init; }

	/// <summary>The user secrets id; when unset it comes from <see cref="SecretsAssembly"/>, else from the nearest project file.</summary>
	public string? UserSecretsId { get; init; }

	/// <summary>An assembly whose <c>[UserSecretsId]</c> names the user secrets id, typically the app under test's.</summary>
	public Assembly? SecretsAssembly { get; init; }

	/// <summary>The folder holding <c>{id}/secrets.json</c>; defaults to where <c>dotnet user-secrets</c> keeps them.</summary>
	public string? UserSecretsRoot { get; init; }

	/// <summary>Headers to write on top of <c>content-type</c> and <c>location</c>; credential headers are never written.</summary>
	public IReadOnlyCollection<string> Headers { get; init; } = [];

	/// <summary>
	/// The origin (<c>https://docs.example.com</c>) of the site under test. Wherever it appears in the transcript, in a
	/// <c>location</c> header or a body, it is written as <c>{origin}</c>, so two sites compare equal.
	/// </summary>
	public Uri? BaseUrl { get; init; }

	/// <summary>The clock for <c>$datetime</c>, <c>$timestamp</c> and the like.</summary>
	public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

	/// <summary>The randomness for <c>$guid</c> and <c>$randomInt</c>; pass a seeded instance for repeatable requests.</summary>
	public Random? Random { get; init; }

	/// <summary>
	/// <see cref="TranscriptMode.Off"/> sends the requests and fails on a request that fails, without comparing the
	/// transcript or needing a verified file. For a live service whose responses drift.
	/// </summary>
	public TranscriptMode Transcript { get; init; } = TranscriptMode.Compare;

	/// <summary>
	/// A read-only run, for a live service: a request that is not GET, HEAD or OPTIONS is not sent, and fails the run,
	/// unless the request carries <c># @readonly</c> (a JSON-RPC call over POST that only reads, say).
	/// </summary>
	public bool ReadOnly { get; init; }

	/// <summary>
	/// Compares with <c>{file}.{variant}.verified.responses</c> instead of <c>{file}.verified.responses</c>, for output
	/// that legitimately differs per environment or flag set. Letters, digits, <c>.</c>, <c>-</c> and <c>_</c>.
	/// </summary>
	public string? Variant { get; init; }

	/// <summary>The enabled feature flags of the service under test, which <c># @requires</c> is judged against.</summary>
	public IReadOnlySet<string> Features { get; init; } = new HashSet<string>(StringComparer.Ordinal);

	/// <summary>The build type of the service under test, such as <c>assembler</c>; unknown when <see langword="null"/>.</summary>
	public string? BuildType { get; init; }

	/// <summary>The environment of the service under test, such as <c>prod</c> or <c>edge</c>; unknown when <see langword="null"/>.</summary>
	public string? Env { get; init; }

	/// <summary>What <c># @requires</c> lines are judged against.</summary>
	public HttpFileContext Context => new(Features, BuildType, Env);
}

/// <summary>The request a <c>.http</c> run stopped at.</summary>
/// <param name="Line">The 1-based line of the problem: the reference that could not be resolved, else the request line.</param>
/// <param name="Request">The request as written, prefixed with its name when it has one.</param>
/// <param name="Message">What went wrong, redacted.</param>
public sealed record HttpFileFailure(int Line, string Request, string Message)
{
	/// <summary><c>{file}:{line}: {request}</c>, then the message on the next line.</summary>
	public string Describe(string file) => $"{file}:{Line}: {Request}\n{Message}";
}

/// <summary>
/// A <c>.http</c> run's transcript: one section per request, headed by the request as written, then its response.
/// </summary>
/// <remarks>
/// <code>
/// ## login: POST {{host}}/api/login
/// status: 200 OK
/// content-type: application/json; charset=utf-8
///
/// {
///   "token": "{{$secret ApiToken}}"
/// }
///
/// ## GET {{host}}/api/orders
/// status: 404 Not Found
/// </code>
/// Headings keep the variables unresolved, so hosts and secrets never appear and a heading survives a change of host.
/// Every value from a secret, the process environment, a <c>.env</c> file or a private environment file is replaced
/// by the reference that produced it before anything is written.
/// </remarks>
/// <param name="File">The <c>.http</c> file.</param>
/// <param name="Text">The transcript.</param>
/// <param name="Failure">The request the run stopped at, if it did not finish.</param>
public sealed record HttpTranscript(string File, string Text, HttpFileFailure? Failure)
{
	/// <summary>Whether the transcript is compared; <see cref="TranscriptMode.Off"/> checks only that no request failed.</summary>
	public TranscriptMode Mode { get; init; } = TranscriptMode.Compare;

	/// <summary>The variant of the verified file the transcript is compared with, or <see langword="null"/> for the plain one.</summary>
	public string? Variant { get; init; }

	/// <summary>
	/// Renders the exchanges of <paramref name="result"/>, redacted, writing <paramref name="headers"/> on top of the defaults.
	/// With <paramref name="shape"/> the bodies are written as their shape (see <see cref="TranscriptMode.Shape"/>).
	/// </summary>
	public static string Render(HttpFileRunResult result, IEnumerable<string>? headers = null, bool shape = false, Uri? baseUrl = null)
	{
		ArgumentNullException.ThrowIfNull(result);
		var chosen = HttpResponseText.Chosen(headers);
		var text = new StringBuilder();
		foreach (var raw in result.Exchanges)
		{
			var exchange = result.Redact(raw);
			var response = exchange.Response;
			HttpResponseText.Append(
				text,
				new HttpResponseSection(
					Heading(exchange.Request),
					response.StatusCode,
					response.ReasonPhrase,
					[.. response.Headers.Concat(response.ContentHeaders).Select(h => new KeyValuePair<string, string>(h.Name, h.Value))],
					response.Body,
					response.MediaType,
					response.CharSet
				),
				chosen,
				shape
			);
		}

		// Again over the whole text: pretty-printing unescapes JSON strings, so a value a server echoed as "+..."
		// only reads as the value now.
		var redacted = result.Redact(text.ToString());
		return Origin(baseUrl) is { } origin ? redacted.Replace(origin, OriginToken, StringComparison.Ordinal) : redacted;
	}

	/// <summary>What a site's origin is written as.</summary>
	public const string OriginToken = "{origin}";

	/// <summary>The origin of an http(s) URL, without a trailing slash; <see langword="null"/> for any other.</summary>
	internal static string? Origin(Uri? baseUrl) => baseUrl is { Scheme: "http" or "https" } ? baseUrl.GetLeftPart(UriPartial.Authority) : null;

	/// <summary>Parses a transcript into its sections, as <see cref="HttpSnapshotFormat.Parse"/> does.</summary>
	/// <exception cref="FormatException">The text is not a transcript.</exception>
	public static Trees.SeqNode Parse(string text) => HttpResponseText.Parse(text);

	internal static string Heading(HttpFileRequest request) => request.Name is { Length: > 0 } name ? $"{name}: {request.RequestLine}" : request.RequestLine;
}

/// <summary>
/// Runs <c>.http</c> files and snapshots every response. The snapshot belongs to the file, so a test and
/// <c>snap http run</c> compare against the same <c>.snapshots/{path}.verified.responses</c>.
/// </summary>
public static partial class HttpFileSnapshot
{
	/// <summary>The <c>.responses</c> format.</summary>
	public static HttpSnapshotFormat Format => HttpSnapshot.Format;

	/// <summary>
	/// The <c>.http</c> files under <paramref name="folder"/> of the test project, as paths relative to the project
	/// with <c>/</c> separators, sorted. Use it as a test's data source.
	/// </summary>
	public static IReadOnlyList<string> Find(string folder = "http", [CallerFilePath] string callerFilePath = "")
	{
		var project = ProjectDirectory(callerFilePath);
		var root = Path.Combine(project, folder);
		if (!Directory.Exists(root))
			return [];
		return [
			.. Directory
				.EnumerateFiles(root, "*.http", SearchOption.AllDirectories)
				.Select(f => Path.GetRelativePath(project, f).Replace('\\', '/'))
				.Order(StringComparer.Ordinal)
		];
	}

	/// <summary>
	/// Runs every request of a <c>.http</c> file. A relative <paramref name="httpFile"/> is relative to the test project.
	/// A request that cannot be resolved, sent or read stops the run; the transcript then holds the responses before it
	/// and the failure. An error status is a response, not a failure.
	/// </summary>
	/// <exception cref="HttpFileParseException">The file has syntax errors.</exception>
	public static Task<HttpTranscript> RunAsync(
		string httpFile,
		HttpFileSnapshotOptions? options = null,
		CancellationToken cancellationToken = default,
		[CallerFilePath] string callerFilePath = ""
	)
	{
		ArgumentException.ThrowIfNullOrEmpty(httpFile);
		var file = Path.IsPathRooted(httpFile) ? httpFile : Path.Combine(ProjectDirectory(callerFilePath), httpFile);
		return RunFileAsync(file, options, cancellationToken);
	}

	/// <summary>
	/// Why a <c>.http</c> file should not run in <paramref name="context"/>: the first <c># @requires</c> condition it does not
	/// meet, as written (<c>@requires !env:prod</c>). <see langword="null"/> when it applies. A file without directives always applies.
	/// </summary>
	/// <param name="httpFile">A <c>.http</c> file; a relative path is relative to the test project, as for <c>RunAsync</c>.</param>
	/// <param name="context">What the run knows about the service.</param>
	/// <param name="callerFilePath">Filled in by the compiler.</param>
	/// <exception cref="HttpFileParseException">The file has syntax errors.</exception>
	public static string? Unmet(string httpFile, HttpFileContext context, [CallerFilePath] string callerFilePath = "")
	{
		ArgumentException.ThrowIfNullOrEmpty(httpFile);
		ArgumentNullException.ThrowIfNull(context);
		return LoadMetadata(httpFile, callerFilePath).Unmet(context) is { } unmet ? $"@requires {unmet}" : null;
	}

	/// <summary>
	/// Why a <c>.http</c> file should be skipped in a run with <paramref name="options"/>, or <see langword="null"/> when it
	/// should run: an unmet <c># @requires</c> condition, or <c># @mutates</c> on a <see cref="HttpFileSnapshotOptions.ReadOnly"/> run.
	/// </summary>
	public static string? SkipReason(string httpFile, HttpFileSnapshotOptions options, [CallerFilePath] string callerFilePath = "")
	{
		ArgumentException.ThrowIfNullOrEmpty(httpFile);
		ArgumentNullException.ThrowIfNull(options);
		var metadata = LoadMetadata(httpFile, callerFilePath);
		if (metadata.Unmet(options.Context) is { } unmet)
			return $"@requires {unmet}";
		return metadata.Mutates && options.ReadOnly ? "@mutates, and the run is read-only" : null;
	}

	/// <summary>The file's header directives: tags, requirements, monitor, alerts, <c># @mutates</c>.</summary>
	public static HttpFileMetadata Metadata(string httpFile, [CallerFilePath] string callerFilePath = "")
	{
		ArgumentException.ThrowIfNullOrEmpty(httpFile);
		return LoadMetadata(httpFile, callerFilePath);
	}

	private static HttpFileMetadata LoadMetadata(string httpFile, string callerFilePath) =>
		HttpFile.Load(Path.IsPathRooted(httpFile) ? httpFile : Path.Combine(ProjectDirectory(callerFilePath), httpFile)).Metadata;

	/// <summary>Runs a <c>.http</c> file at a full path, or relative to the working folder.</summary>
	internal static async Task<HttpTranscript> RunFileAsync(string httpFile, HttpFileSnapshotOptions? options, CancellationToken cancellationToken)
	{
		options ??= new HttpFileSnapshotOptions();
		var full = Path.GetFullPath(httpFile);
		var file = HttpFile.Load(full);
		var result = await HttpFileRunner.RunAsync(
			file,
			options.Client,
			new HttpFileRunOptions
			{
				Environment = options.Environment,
				Variables = options.Variables,
				Secrets = options.Secrets,
				UserSecretsId = options.UserSecretsId,
				SecretsAssembly = options.SecretsAssembly,
				UserSecretsRoot = options.UserSecretsRoot,
				TimeProvider = options.TimeProvider,
				Random = options.Random,
				ReadOnly = options.ReadOnly,
			},
			cancellationToken
		).ConfigureAwait(false);
		var failure = result.Failure is { } f ? new HttpFileFailure(f.Line, HttpTranscript.Heading(f.Request), result.Redact(f.Message)) : null;
		return new HttpTranscript(full, HttpTranscript.Render(result, options.Headers, options.Transcript == TranscriptMode.Shape, options.BaseUrl), failure)
		{
			// The text is already the shape, so it is compared like any other.
			Mode = options.Transcript == TranscriptMode.Shape ? TranscriptMode.Compare : options.Transcript,
			Variant = options.Variant,
		};
	}

	/// <summary>
	/// Compares the transcript with the file's verified snapshot. A run that stopped at a failing request fails even
	/// when its partial transcript matches, and the message names the request first.
	/// </summary>
	/// <param name="transcript">The run to compare.</param>
	/// <param name="options">Per-call snapshot options such as scrubbers.</param>
	/// <param name="because">An optional reason, shown in the failure message.</param>
	/// <param name="diff">How trees are compared: ignored paths such as <c>$[*].body.items[*].score</c>, strict numbers.</param>
	public static SnapshotResult Match(HttpTranscript transcript, SnapshotOptions? options = null, string? because = null, TreeDiffOptions? diff = null)
	{
		ArgumentNullException.ThrowIfNull(transcript);
		if (transcript.Mode == TranscriptMode.Off)
			return WithFailure(transcript, new SnapshotResult(true, null, string.Empty, string.Empty, string.Empty));
		return WithFailure(
			transcript,
			Snapshot.MatchFile(transcript.Text, HttpSnapshot.FormatWith(diff), transcript.File, options, because, transcript.Variant)
		);
	}

	internal static SnapshotResult WithFailure(HttpTranscript transcript, SnapshotResult result)
	{
		if (transcript.Failure is not { } failure)
			return result;
		var stopped = $"Request stopped at {failure.Describe(Relative(transcript.File))}";
		return result with { Passed = false, FailureMessage = result.FailureMessage is { } message ? stopped + "\n" + message : stopped, };
	}

	// Messages use paths relative to the working folder when the file is below it, as the rest of the library's do.
	private static string Relative(string file)
	{
		var relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), file);
		return (relative.StartsWith("..", StringComparison.Ordinal) ? file : relative).Replace('\\', '/');
	}

	/// <summary>
	/// The test project's folder: from the assembly metadata Nullean.Snapshots' build targets write, else the nearest
	/// folder holding a <c>*.csproj</c> above <paramref name="callerFilePath"/>.
	/// </summary>
	internal static string ProjectDirectory(string callerFilePath)
	{
		var fromMetadata = Assembly.GetEntryAssembly()?.GetCustomAttributes<AssemblyMetadataAttribute>().LastOrDefault(
			a => a.Key == "Nullean.Snapshots.ProjectDirectory"
		)?.Value;
		if (!string.IsNullOrWhiteSpace(fromMetadata))
			return fromMetadata;
		for (var dir = Path.GetDirectoryName(callerFilePath); !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
		{
			if (Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*.csproj").Any())
				return dir;
		}

		throw new InvalidOperationException(
			$"Cannot find the test project for '{callerFilePath}'. Reference Nullean.Snapshots from the test project so its build targets record it."
		);
	}
}
