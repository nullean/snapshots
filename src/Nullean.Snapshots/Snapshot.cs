using System.Collections.Concurrent;
using System.IO.Abstractions;
using System.Reflection;
using Nullean.ScopedFileSystem;

namespace Nullean.Snapshots;

/// <summary>The outcome of one snapshot comparison.</summary>
/// <param name="Passed">Whether the assertion passes.</param>
/// <param name="FailureMessage">The plain-text failure message when it does not.</param>
/// <param name="Key">The snapshot's key, as <c>snap</c> takes it.</param>
/// <param name="VerifiedPath">The verified file, full path.</param>
/// <param name="ReceivedPath">The received file, full path.</param>
public sealed record SnapshotResult(bool Passed, string? FailureMessage, string Key, string VerifiedPath, string ReceivedPath);

/// <summary>
/// Compares values with their verified snapshots. Assertion packages such as <c>Nullean.Snapshots.AwesomeAssertions</c>
/// call this and turn a failed <see cref="SnapshotResult"/> into an assertion failure.
/// </summary>
public static class Snapshot
{
	private static readonly SnapshotEngine Engine = CreateEngine();

	private static SnapshotEngine CreateEngine()
	{
		var engine = new SnapshotEngine(new FileSystem(), Environment.GetEnvironmentVariable);
		// The run manifests are written once, when the test process ends, rather than on every assertion.
		AppDomain.CurrentDomain.ProcessExit += (_, _) => engine.FlushManifests();
		return engine;
	}

	/// <summary>Lets a test run the assertion packages against its own engine, without touching other tests.</summary>
	internal static AsyncLocal<SnapshotEngine?> EngineOverride { get; } = new();

	/// <summary>The engine assertions use: the test's own when it set one, else the process's.</summary>
	internal static SnapshotEngine Current => EngineOverride.Value ?? Engine;

	/// <summary>
	/// Compares <paramref name="actual"/> with its verified snapshot. On a match a stale received file is removed. On a
	/// mismatch the received file is written and the result carries the failure message, unless
	/// <c>SNAPSHOT_ACCEPT=1</c> (and not <c>CI</c>) makes it write the verified file instead.
	/// </summary>
	/// <param name="actual">The value, as text.</param>
	/// <param name="format">How it is stored and compared.</param>
	/// <param name="callSite">Where the assertion is.</param>
	/// <param name="name">Tells apart several snapshots in one test: letters, digits, <c>-</c>, <c>_</c> and <c>.</c>.</param>
	/// <param name="options">Per-call options such as scrubbers.</param>
	/// <param name="because">An optional reason, shown in the failure message.</param>
	public static SnapshotResult Match(
		string actual,
		SnapshotFormat format,
		SnapshotCallSite callSite,
		string? name = null,
		SnapshotOptions? options = null,
		string? because = null
	) => (EngineOverride.Value ?? Engine).Match(actual, format, callSite, name, options, because);

	/// <summary>
	/// Compares <paramref name="actual"/> with the verified snapshot of a source file, such as a browser journey,
	/// rather than of the calling test. The key is the file's path in its project without the extension
	/// (<c>{Project}/{dir}/{name}</c>), so every runner of the file, a test or a command line tool, shares one verified
	/// file. Otherwise it behaves as <see cref="Match(string, SnapshotFormat, SnapshotCallSite, string?, SnapshotOptions?, string?)"/>.
	/// </summary>
	/// <param name="actual">The value, as text.</param>
	/// <param name="format">How it is stored and compared.</param>
	/// <param name="sourceFile">
	/// The file the snapshot belongs to: a full path inside a project, or a path relative to the test project's
	/// directory.
	/// </param>
	/// <param name="options">Per-call options such as scrubbers.</param>
	/// <param name="because">An optional reason, shown in the failure message.</param>
	/// <param name="variant">
	/// Keeps a separate verified file for output that legitimately differs, such as per feature flag set:
	/// <c>{name}.{variant}.verified.{ext}</c>. Letters, digits, <c>.</c>, <c>-</c> and <c>_</c>.
	/// </param>
	public static SnapshotResult MatchFile(
		string actual,
		SnapshotFormat format,
		string sourceFile,
		SnapshotOptions? options = null,
		string? because = null,
		string? variant = null
	) => (EngineOverride.Value ?? Engine).MatchFile(actual, format, sourceFile, options, because, variant);

	/// <summary>
	/// An empty folder for the files a run of <paramref name="sourceFile"/> writes, such as a journey's screenshots and
	/// HAR files: <c>{received}/{folder}/{name}.artifacts</c>, next to its received snapshot. It is emptied first.
	/// <paramref name="sourceFile"/> resolves as for <see cref="MatchFile"/>.
	/// </summary>
	public static string ArtifactsDirectory(string sourceFile) => (EngineOverride.Value ?? Engine).ArtifactsDirectory(sourceFile);

	/// <summary>
	/// Compares <paramref name="actual"/> with an expected value written in the test's own source, as the string literal
	/// <paramref name="expected"/>. A mismatch writes a received record that <c>snap accept</c> turns into an edit of that
	/// literal; <c>SNAPSHOT_ACCEPT=1</c> (never in CI) edits it when the test process ends. Otherwise it behaves as
	/// <see cref="Match(string, SnapshotFormat, SnapshotCallSite, string?, SnapshotOptions?, string?)"/>.
	/// </summary>
	/// <param name="actual">The value, as text.</param>
	/// <param name="expected">The literal's value, or null when the argument was left out (a new snapshot).</param>
	/// <param name="format">How it is compared.</param>
	/// <param name="site">Where the assertion is and how its <c>expected</c> argument reads in the source.</param>
	/// <param name="options">Per-call options such as scrubbers; they apply to <paramref name="actual"/> only.</param>
	/// <param name="because">An optional reason, shown in the failure message.</param>
	public static SnapshotResult MatchInline(
		string actual,
		string? expected,
		SnapshotFormat format,
		InlineSnapshotCallSite site,
		SnapshotOptions? options = null,
		string? because = null
	) => (EngineOverride.Value ?? Engine).MatchInline(actual, expected, format, site, options, because);

	/// <summary>
	/// Compares <paramref name="actual"/> bytes with their verified snapshot, as the text overload does but without
	/// scrubbers or normalisation. On a mismatch the format's attachments, such as a diff image, are written next to
	/// the received file.
	/// </summary>
	/// <param name="actual">The value's bytes, stored exactly as given.</param>
	/// <param name="format">How it is stored and compared.</param>
	/// <param name="callSite">Where the assertion is.</param>
	/// <param name="name">Tells apart several snapshots in one test: letters, digits, <c>-</c>, <c>_</c> and <c>.</c>.</param>
	/// <param name="because">An optional reason, shown in the failure message.</param>
	public static SnapshotResult Match(
		ReadOnlyMemory<byte> actual,
		BinarySnapshotFormat format,
		SnapshotCallSite callSite,
		string? name = null,
		string? because = null
	) => (EngineOverride.Value ?? Engine).Match(actual, format, callSite, name, because);
}

/// <summary>Where an inline snapshot assertion is, and how its <c>expected</c> argument reads in the source.</summary>
/// <param name="CallSite">The compiler-filled file, member and line of the call.</param>
/// <param name="MethodName">The assertion method the test called, e.g. <c>MatchInlineSnapshot</c>; it is searched for in the source.</param>
/// <param name="ExpectedExpression">
/// The source text of the <c>expected</c> argument (<see cref="System.Runtime.CompilerServices.CallerArgumentExpressionAttribute"/>),
/// or null when the test left it out.
/// </param>
public readonly record struct InlineSnapshotCallSite(SnapshotCallSite CallSite, string MethodName, string? ExpectedExpression);

/// <summary>The snapshot workflow over an <see cref="IFileSystem"/>, so tests can run it on a mock.</summary>
internal sealed partial class SnapshotEngine(
	IFileSystem fileSystem,
	Func<string, string?> getVariable,
	Func<ISnapshotTestIdentityProvider?>? identityProvider = null,
	Func<SnapshotCallSite, Assembly?, SnapshotProject>? projectResolver = null
)
{
	private readonly SnapshotProjectResolver _resolver = new(fileSystem);
	private readonly ConcurrentDictionary<string, string> _owners = new(StringComparer.OrdinalIgnoreCase);
	private readonly ConcurrentDictionary<SnapshotProject, IFileSystem> _scoped = new();
	private readonly ConcurrentDictionary<SnapshotProject, RunManifest> _manifests = new();
	private int _flushed;

	// Projects whose received folder already has its marker in this process. Writing it once, from whichever failure
	// gets there first, keeps parallel failures from replacing the same file at once, which Windows refuses.
	private readonly ConcurrentDictionary<SnapshotProject, byte> _markers = new();

	public SnapshotResult Match(string actual, SnapshotFormat format, SnapshotCallSite callSite, string? name, SnapshotOptions? options, string? because)
	{
		ArgumentNullException.ThrowIfNull(actual);
		ArgumentNullException.ThrowIfNull(format);
		var (project, key, identity) = ResolveTestKey(callSite, name);
		ClaimKey(key, identity, callSite);
		return Compare(actual, format, project, key, identity?.TreeNodeFilter, options, because);
	}

	public SnapshotResult Match(ReadOnlyMemory<byte> actual, BinarySnapshotFormat format, SnapshotCallSite callSite, string? name, string? because)
	{
		ArgumentNullException.ThrowIfNull(format);
		format.Validate(actual.Span);
		var (project, key, identity) = ResolveTestKey(callSite, name);
		ClaimKey(key, identity, callSite);
		return CompareBinary(actual, format, Target(project, key, identity?.TreeNodeFilter, format.Extension), because);
	}

	/// <summary>The project and key of a snapshot named after the calling test.</summary>
	private (SnapshotProject Project, string Key, SnapshotTestIdentity? Identity) ResolveTestKey(SnapshotCallSite callSite, string? name)
	{
		if (name is not null && !SnapshotPath.IsValidName(name))
			throw new ArgumentException($"Snapshot name '{name}' may only hold letters, digits, '-', '_' and inner '.'.", nameof(name));

		var identity = (identityProvider is null ? SnapshotDefaults.TestIdentity : identityProvider())?.Current;
		var project = projectResolver is null
			? _resolver.Resolve(identity?.TestAssembly ?? Assembly.GetEntryAssembly(), callSite.FilePath)
			: projectResolver(callSite, identity?.TestAssembly);

		var key = SnapshotPath.Key(
			project.Name,
			SnapshotPath.RelativeTestDirectory(fileSystem, project.Directory, callSite.FilePath),
			identity?.ClassName ?? fileSystem.Path.GetFileNameWithoutExtension(callSite.FilePath),
			identity?.MethodName ?? callSite.MemberName,
			name,
			identity is null ? null : SnapshotPath.ArgumentsHash(identity.Arguments)
		);
		return (project, key, identity);
	}

	public SnapshotResult MatchInline(
		string actual,
		string? expected,
		SnapshotFormat format,
		InlineSnapshotCallSite site,
		SnapshotOptions? options,
		string? because
	)
	{
		ArgumentNullException.ThrowIfNull(actual);
		ArgumentNullException.ThrowIfNull(format);
		ArgumentException.ThrowIfNullOrWhiteSpace(site.MethodName);
		var callSite = site.CallSite;
		var (project, key, identity) = ResolveTestKey(callSite, InlineName(callSite.LineNumber));
		ClaimKey(key, identity, callSite);
		// No verified file to record, but the project still gets a manifest, so 'snap orphans' has one to read.
		_manifests.GetOrAdd(project, static _ => new RunManifest(DateTimeOffset.UtcNow));
		var target = Target(project, key, identity?.TreeNodeFilter, InlineExtension, touch: false);
		var fs = target.Fs;

		var value = SnapshotText.Normalize(Scrub(actual, options));
		var expectedText = expected is null ? null : SnapshotText.Normalize(expected);
		var comparison = expectedText is null ? null : format.Compare(expectedText, value);
		if (comparison is { IsMatch: true })
		{
			DeleteIfExists(fs, target.ReceivedPath);
			return target.Passed();
		}

		var source = SourcePath(project, callSite.FilePath);
		var edit = new InlineEdit(site.MethodName, callSite.LineNumber, site.ExpectedExpression, expected, value);
		var sourceHash = TryHashSource(project, source);
		var record = InlineRecord.Render(SnapshotPath.Relative(fileSystem, project.ReceivedDirectory, source), format.Name, edit, sourceHash);
		var environment = SnapshotEnvironment.From(getVariable);
		string? note = null;
		if (environment.Mode == SnapshotMode.Accept)
		{
			note = TryQueueInline(project, target, source, edit, record, sourceHash);
			if (note is null)
			{
				DeleteIfExists(fs, target.ReceivedPath);
				return target.Passed();
			}
		}

		PrepareReceived(target);
		AtomicFile.Write(fs, target.ReceivedPath, record);
		var location = SnapshotPath.Relative(fileSystem, project.RepositoryRoot, source) + ":" + callSite.LineNumber.ToString(
			System.Globalization.CultureInfo.InvariantCulture
		);
		return Failed(target, format.Name, comparison, because, environment, [], new InlineFailure(location, site.MethodName, note));
	}

	/// <summary>The caller's file as a full path; a deterministic-build path (<c>/_/…</c>) is mapped back into the project.</summary>
	private string SourcePath(SnapshotProject project, string callerFilePath)
	{
		if (!SnapshotPath.IsDeterministic(callerFilePath))
			return fileSystem.Path.GetFullPath(callerFilePath);
		var directory = SnapshotPath.RelativeTestDirectory(fileSystem, project.Directory, callerFilePath);
		var parts = directory.Length == 0 ? [] : directory.Split('/');
		return fileSystem.Path.Combine([project.Directory, .. parts, fileSystem.Path.GetFileName(callerFilePath)]);
	}

	/// <summary>
	/// Under <c>SNAPSHOT_ACCEPT=1</c>: checks the literal can be rewritten and queues the edit for the end of the process,
	/// so a test run never edits a file other tests are being compiled from or read. Returns why it cannot, or null.
	/// </summary>
	private string? TryQueueInline(SnapshotProject project, SnapshotTarget target, string source, InlineEdit edit, string record, string? sourceHash)
	{
		try
		{
			if (!source.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
				throw new InlineSnapshotException($"{fileSystem.Path.GetFileName(source)} is not a C# file.");
			var relative = SnapshotPath.Relative(fileSystem, project.Directory, source);
			if (relative.StartsWith("../", StringComparison.Ordinal) || fileSystem.Path.IsPathRooted(relative))
				throw new InlineSnapshotException("the test file is outside the test project, which is where snapshots are allowed to edit.");
			var text = InlineSnapshotSource.ReadText(ScopeForSources(project).File.ReadAllBytes(source), out _);
			var start = InlineSnapshotSource.Check(text, edit, out var current);
			if (!InlineSnapshotSource.SameValue(current, edit.Expected))
				throw new InlineSnapshotException("the source changed since the test project was built; build and run again.");
			var queued = _inline.GetOrAdd((source, start), _ => new QueuedInline(project, target, source, edit, record, sourceHash));
			if (!string.Equals(queued.Edit.NewValue, edit.NewValue, StringComparison.Ordinal))
				throw new InlineSnapshotException(
					"test cases of one test write different values to this snapshot; use MatchSnapshot for values that differ per case."
				);
			return null;
		}
		catch (InlineSnapshotException e)
		{
			return "Not accepted: " + e.Message;
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
		{
			return "Not accepted: " + e.Message;
		}
	}

	private sealed record QueuedInline(SnapshotProject Project, SnapshotTarget Target, string Source, InlineEdit Edit, string Record, string? SourceHash);

	private readonly ConcurrentDictionary<(string Source, int Start), QueuedInline> _inline = new();

	/// <summary>Applies the edits queued by <c>SNAPSHOT_ACCEPT=1</c>, one write per file. Failures go to stderr and become received records.</summary>
	private void FlushInline()
	{
		foreach (var file in _inline.Values.GroupBy(q => q.Source, StringComparer.Ordinal))
		{
			var queued = file.ToList();
			try
			{
				var fs = ScopeForSources(queued[0].Project);
				var bytes = fs.File.ReadAllBytes(file.Key);
				var hash = InlineSnapshotSource.Hash(bytes);
				var text = InlineSnapshotSource.ReadText(bytes, out var bom);
				// Edits were located in the file as the assertions read it; if it changed since, none of them is safe.
				var changed = queued.Any(q => q.SourceHash is not null && q.SourceHash != hash);
				var rewrite = changed ? null : InlineSnapshotSource.Rewrite(text, [.. queued.Select(q => q.Edit)]);
				for (var i = 0; i < queued.Count; i++)
				{
					var error = changed ? "the file changed while the tests ran." : rewrite!.Errors[i];
					if (error is null)
						continue;
					Console.Error.WriteLine($"Nullean.Snapshots: {queued[i].Target.Key} was not accepted: {error} See 'snap status'.");
					PrepareReceived(queued[i].Target);
					AtomicFile.Write(queued[i].Target.Fs, queued[i].Target.ReceivedPath, queued[i].Record);
				}

				if (rewrite is not null && rewrite.Errors.Any(e => e is null))
					AtomicFile.Write(fs, file.Key, InlineSnapshotSource.ToBytes(rewrite.Source, bom));
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or InlineSnapshotException)
			{
				Console.Error.WriteLine($"Nullean.Snapshots: could not update {file.Key}: {e.Message}");
				foreach (var q in queued)
				{
					try
					{
						PrepareReceived(q.Target);
						AtomicFile.Write(q.Target.Fs, q.Target.ReceivedPath, q.Record);
					}
					catch (Exception inner) when (inner is IOException or UnauthorizedAccessException or System.Security.SecurityException)
					{
						// Nothing more to do while the process exits.
					}
				}
			}
		}
	}

	/// <summary>The hash of the test file's bytes for the record, or null when it cannot be read (then accepting skips that check).</summary>
	private string? TryHashSource(SnapshotProject project, string source)
	{
		try
		{
			return InlineSnapshotSource.Hash(ScopeForSources(project).File.ReadAllBytes(source));
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
		{
			return null;
		}
	}

	/// <summary>Source files are the one thing besides snapshot folders the library may write, and only inline snapshots do.</summary>
	private IFileSystem ScopeForSources(SnapshotProject project) =>
		_sourceScoped.GetOrAdd(project, p => new ScopedFileSystem.ScopedFileSystem(fileSystem, new ScopedFileSystemOptions(p.Directory)));

	private readonly ConcurrentDictionary<SnapshotProject, IFileSystem> _sourceScoped = new();

	/// <summary>The received file's extension, and the name of the line segment in the key (<c>Method.L42</c>).</summary>
	internal const string InlineExtension = "inline";

	internal static string InlineName(int line) => "L" + line.ToString(System.Globalization.CultureInfo.InvariantCulture);

	public SnapshotResult MatchFile(string actual, SnapshotFormat format, string sourceFile, SnapshotOptions? options, string? because, string? variant = null)
	{
		ArgumentNullException.ThrowIfNull(actual);
		ArgumentNullException.ThrowIfNull(format);
		ArgumentException.ThrowIfNullOrWhiteSpace(sourceFile);
		if (variant is not null && !SnapshotPath.IsValidName(variant))
			throw new ArgumentException(
				$"The variant '{variant}' must be a non-empty name of letters, digits, '.', '-' and '_', without a leading or trailing dot.",
				nameof(variant)
			);

		var identity = (identityProvider is null ? SnapshotDefaults.TestIdentity : identityProvider())?.Current;
		var (project, fullPath, relative, name) = ResolveFile(sourceFile, identity?.TestAssembly ?? Assembly.GetEntryAssembly());
		var slash = relative.LastIndexOf('/');
		var key = SnapshotPath.Key(project.Name, slash < 0 ? string.Empty : relative[..slash], string.Empty, name, variant, null);

		ClaimKey(key, identity, new SnapshotCallSite(fullPath, string.Empty, 0));
		return Compare(actual, format, project, key, identity?.TreeNodeFilter, options, because);
	}

	/// <summary>
	/// An empty folder for the files a run of <paramref name="sourceFile"/> writes, such as a journey's screenshots:
	/// <c>{received}/{folder in the project}/{name}.artifacts</c>. Emptied first, so files from an earlier run never
	/// linger. Inside the received folder, so the snapshot library still writes nowhere else.
	/// </summary>
	public string ArtifactsDirectory(string sourceFile)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sourceFile);
		var identity = (identityProvider is null ? SnapshotDefaults.TestIdentity : identityProvider())?.Current;
		var (project, _, relative, name) = ResolveFile(sourceFile, identity?.TestAssembly ?? Assembly.GetEntryAssembly());
		var slash = relative.LastIndexOf('/');
		var parts = slash < 0 ? [] : relative[..slash].Split('/');
		var folder = fileSystem.Path.Combine([project.ReceivedDirectory, .. parts, name + ArtifactsSuffix]);
		var fs = ScopeFor(project);
		fs.Directory.CreateDirectory(project.ReceivedDirectory);
		if (_markers.TryAdd(project, 0))
			ReceivedMarker.For(fileSystem, project).EnsureWritten(fs, project.ReceivedDirectory);
		if (fs.Directory.Exists(folder))
			fs.Directory.Delete(folder, true);
		fs.Directory.CreateDirectory(folder);
		return folder;
	}

	/// <summary>A source file's project, full path, path within the project (<c>/</c> separators) and sanitised name.</summary>
	private (SnapshotProject Project, string FullPath, string Relative, string Name) ResolveFile(string sourceFile, Assembly? assembly)
	{
		SnapshotProject project;
		string fullPath;
		if (fileSystem.Path.IsPathRooted(sourceFile))
		{
			fullPath = fileSystem.Path.GetFullPath(sourceFile);
			project = projectResolver is null
				? _resolver.Resolve(assembly, fullPath)
				: projectResolver(new SnapshotCallSite(fullPath, string.Empty, 0), assembly);
		}
		else
		{
			project = (projectResolver is null
				? assembly is null ? null : _resolver.FromAssembly(assembly)
				: projectResolver(new SnapshotCallSite(sourceFile, string.Empty, 0), assembly)) ??
				throw new InvalidOperationException(
					$"Cannot resolve '{sourceFile}' without a test project. Pass a full path, or reference Nullean.Snapshots from the test project."
				);
			fullPath = fileSystem.Path.GetFullPath(fileSystem.Path.Combine(project.Directory, sourceFile));
		}

		var relative = SnapshotPath.Relative(fileSystem, project.Directory, fullPath);
		if (relative.StartsWith("../", StringComparison.Ordinal) || relative == ".." || fileSystem.Path.IsPathRooted(relative))
			throw new ArgumentException($"'{sourceFile}' is not inside the test project at {project.Directory}.", nameof(sourceFile));
		var name = SnapshotPath.Sanitize(fileSystem.Path.GetFileNameWithoutExtension(fullPath));
		if (name.Trim('.').Length == 0)
			throw new ArgumentException($"'{sourceFile}' has no file name to key its snapshot by.", nameof(sourceFile));
		return (project, fullPath, relative, name);
	}

	/// <summary>Ends a source file's artifacts folder; a dot never appears in a test class's folder name.</summary>
	internal const string ArtifactsSuffix = ".artifacts";

	private SnapshotResult Compare(
		string actual,
		SnapshotFormat format,
		SnapshotProject project,
		string key,
		string? treeNodeFilter,
		SnapshotOptions? options,
		string? because
	)
	{
		var target = Target(project, key, treeNodeFilter, format.Extension);
		var fs = target.Fs;
		var value = SnapshotText.Normalize(Scrub(actual, options));
		var verified = fs.File.Exists(target.VerifiedPath) ? SnapshotText.Normalize(fs.File.ReadAllText(target.VerifiedPath, SnapshotText.Encoding)) : null;
		var comparison = verified is null ? null : format.Compare(verified, value);

		if (comparison is { IsMatch: true })
		{
			DeleteIfExists(fs, target.ReceivedPath);
			return target.Passed();
		}

		var environment = SnapshotEnvironment.From(getVariable);
		if (environment.Mode == SnapshotMode.Accept)
		{
			AtomicFile.Write(fs, target.VerifiedPath, value);
			DeleteIfExists(fs, target.ReceivedPath);
			return target.Passed();
		}

		PrepareReceived(target);
		AtomicFile.Write(fs, target.ReceivedPath, value);
		return Failed(target, format.Name, comparison, because, environment, []);
	}

	private SnapshotResult CompareBinary(ReadOnlyMemory<byte> actual, BinarySnapshotFormat format, SnapshotTarget target, string? because)
	{
		var fs = target.Fs;
		BinarySnapshotComparison? comparison = null;
		if (fs.File.Exists(target.VerifiedPath))
		{
			var verified = fs.File.ReadAllBytes(target.VerifiedPath);
			comparison = format.Compare(verified, actual.Span);
			var undeclared = comparison.Attachments.FirstOrDefault(a => !format.AttachmentSuffixes.Contains(a.Suffix, StringComparer.Ordinal));
			if (undeclared is not null)
				throw new InvalidOperationException(
					$"{format.Name} returned attachment '{undeclared.Suffix}', which it does not declare in AttachmentSuffixes."
				);
		}

		if (comparison is { Comparison.IsMatch: true })
		{
			DeleteReceived(target, format);
			return target.Passed();
		}

		var environment = SnapshotEnvironment.From(getVariable);
		if (environment.Mode == SnapshotMode.Accept)
		{
			AtomicFile.Write(fs, target.VerifiedPath, actual);
			DeleteReceived(target, format);
			return target.Passed();
		}

		PrepareReceived(target);
		AtomicFile.Write(fs, target.ReceivedPath, actual);
		var attachments = new List<(string Label, string Path)>();
		foreach (var suffix in format.AttachmentSuffixes)
		{
			var path = AttachmentPath(target, suffix);
			var attachment = comparison?.Attachments.FirstOrDefault(a => string.Equals(a.Suffix, suffix, StringComparison.Ordinal));
			if (attachment is null)
			{
				DeleteIfExists(fs, path);
			}
			else
			{
				AtomicFile.Write(fs, path, attachment.Content);
				attachments.Add((attachment.Label, path));
			}
		}

		return Failed(target, format.Name, comparison?.Comparison, because, environment, attachments);
	}

	/// <summary>Both paths of a key, the scoped file system, and the manifest entry for the verified file.</summary>
	private SnapshotTarget Target(SnapshotProject project, string key, string? treeNodeFilter, string extension, bool touch = true)
	{
		var withinProject = SnapshotPath.KeyWithinProject(key);
		var verifiedPath = SnapshotPath.File(fileSystem, project.SnapshotsDirectory, withinProject, SnapshotPath.VerifiedInfix, extension);
		var receivedPath = SnapshotPath.File(fileSystem, project.ReceivedDirectory, withinProject, SnapshotPath.ReceivedInfix, extension);
		var fs = ScopeFor(project);
		if (touch)
			Touch(project, withinProject + SnapshotPath.VerifiedInfix + extension);
		return new SnapshotTarget(project, treeNodeFilter, fs, key, withinProject, verifiedPath, receivedPath);
	}

	private void DeleteReceived(SnapshotTarget target, BinarySnapshotFormat format)
	{
		DeleteIfExists(target.Fs, target.ReceivedPath);
		foreach (var suffix in format.AttachmentSuffixes)
			DeleteIfExists(target.Fs, AttachmentPath(target, suffix));
	}

	private string AttachmentPath(SnapshotTarget target, string suffix) =>
		SnapshotPath.File(fileSystem, target.Project.ReceivedDirectory, target.KeyWithinProject, SnapshotPath.AttachmentInfix, suffix);

	/// <summary>Creates the received folder and, once per project in this process, its marker.</summary>
	private void PrepareReceived(SnapshotTarget target)
	{
		target.Fs.Directory.CreateDirectory(target.Project.ReceivedDirectory);
		if (_markers.TryAdd(target.Project, 0))
			ReceivedMarker.For(fileSystem, target.Project).EnsureWritten(target.Fs, target.Project.ReceivedDirectory);
	}

	private SnapshotResult Failed(
		SnapshotTarget target,
		string formatName,
		SnapshotComparison? comparison,
		string? because,
		SnapshotEnvironment environment,
		IReadOnlyList<(string Label, string Path)> attachments,
		InlineFailure? inline = null
	)
	{
		var project = target.Project;
		var failure = new SnapshotFailure(
			formatName,
			SnapshotPath.Relative(fileSystem, project.RepositoryRoot, target.VerifiedPath),
			SnapshotPath.Relative(fileSystem, project.RepositoryRoot, target.ReceivedPath),
			target.Key,
			comparison,
			Because(because),
			project.RelativeDirectory(fileSystem),
			target.TreeNodeFilter,
			environment.AcceptIgnoredInCi
		)
		{ Attachments = [.. attachments.Select(a => (a.Label, SnapshotPath.Relative(fileSystem, project.RepositoryRoot, a.Path)))], Inline = inline, };
		return new SnapshotResult(false, failure.Render(), target.Key, target.VerifiedPath, target.ReceivedPath);
	}

	/// <summary>
	/// Records that this run touched a verified file, for <c>snap orphans</c>. Lock-free and without IO: the manifest is
	/// written by <see cref="FlushManifests"/> when the process ends.
	/// </summary>
	private void Touch(SnapshotProject project, string verifiedFile) =>
		_manifests.GetOrAdd(project, static _ => new RunManifest(DateTimeOffset.UtcNow)).Seen.TryAdd(verifiedFile, 0);

	/// <summary>
	/// Writes each project's manifest of the verified files this run touched, sorted, replacing the previous run's, and
	/// the received folder's marker so the CLI finds the project even when nothing failed. Runs once; never throws, as
	/// it runs while the process exits.
	/// </summary>
	internal void FlushManifests()
	{
		if (Interlocked.Exchange(ref _flushed, 1) == 1)
			return;
		foreach (var (project, manifest) in _manifests)
		{
			try
			{
				var fs = ScopeFor(project);
				fs.Directory.CreateDirectory(project.ReceivedDirectory);
				ReceivedMarker.For(fileSystem, project).EnsureWritten(fs, project.ReceivedDirectory);
				AtomicFile.Write(fs, fs.Path.Combine(project.ReceivedDirectory, RunManifest.FileName), manifest.Render());
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
			{
				// Best effort: a missing manifest only means 'snap orphans' asks for another run.
			}
		}

		FlushInline();
	}

	/// <summary>
	/// Fails when two different tests (or two call sites in one test) resolve to the same key in this run. The same
	/// call site claiming again, as a retried test does, is fine.
	/// </summary>
	private void ClaimKey(string key, SnapshotTestIdentity? identity, SnapshotCallSite callSite)
	{
		var owner = $"{identity?.TestId ?? callSite.FilePath}:{callSite.LineNumber}";
		var existing = _owners.GetOrAdd(key, owner);
		if (!string.Equals(existing, owner, StringComparison.Ordinal))
			throw new InvalidOperationException($"Two snapshot assertions resolve to {key}; pass a name to tell them apart.");
	}

	private static string Scrub(string value, SnapshotOptions? options)
	{
		if (options is not { IgnoreDefaultScrubbers: true })
		{
			foreach (var scrubber in SnapshotDefaults.Scrubbers)
				value = scrubber(value);
		}

		if (options is not null)
		{
			foreach (var scrubber in options.Scrubbers)
				value = scrubber(value);
		}

		return value;
	}

	private static string? Because(string? because)
	{
		if (string.IsNullOrWhiteSpace(because))
			return null;
		because = because.Trim();
		return because.StartsWith("because ", StringComparison.OrdinalIgnoreCase) ? because : "because " + because;
	}

	private static void DeleteIfExists(IFileSystem fs, string path)
	{
		if (fs.File.Exists(path))
			fs.File.Delete(path);
	}

	internal IFileSystem ScopeFor(SnapshotProject project) => _scoped.GetOrAdd(project, Scope);

	/// <summary>
	/// Every write goes through a file system scoped to the project's verified and received folders, so not even a
	/// bug in this library can write anywhere else.
	/// </summary>
	private IFileSystem Scope(SnapshotProject project) =>
		new ScopedFileSystem.ScopedFileSystem(
			fileSystem,
			new ScopedFileSystemOptions(project.SnapshotsDirectory, project.ReceivedDirectory)
			{
				AllowedHiddenFolderNames = [".snapshots", ".artifacts"],
				AllowedHiddenFileNames = [ReceivedMarker.FileName, RunManifest.FileName],
			}
		);
}

/// <summary>Where one assertion's snapshot lives, resolved once per call.</summary>
internal sealed record SnapshotTarget(
	SnapshotProject Project,
	string? TreeNodeFilter,
	IFileSystem Fs,
	string Key,
	string KeyWithinProject,
	string VerifiedPath,
	string ReceivedPath
)
{
	public SnapshotResult Passed() => new(true, null, Key, VerifiedPath, ReceivedPath);
}

/// <summary>
/// The verified files one test run touched, kept as <c>.snapshots-manifest</c> in the project's received folder: a
/// header line, then one path below the snapshots folder per line (<c>Rendering/HtmlTests/RendersTable.verified.html</c>).
/// </summary>
internal sealed class RunManifest(DateTimeOffset started)
{
	public const string FileName = ".snapshots-manifest";
	private const string HeaderPrefix = "# verified files touched by the test run that started ";

	/// <summary>The touched files; a concurrent dictionary used as a lock-free set.</summary>
	public ConcurrentDictionary<string, byte> Seen { get; } = new(StringComparer.Ordinal);

	public string Render() => Header(started) + string.Concat(Seen.Keys.Order(StringComparer.Ordinal).Select(f => f + "\n"));

	public static string Header(DateTimeOffset started) =>
		HeaderPrefix + started.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture) + "\n";

	/// <summary>The paths a manifest lists, and when its run started if the header says.</summary>
	public static (IReadOnlySet<string> Files, string? Started) Read(string text)
	{
		var files = new HashSet<string>(StringComparer.Ordinal);
		string? started = null;
		foreach (var raw in text.Split('\n'))
		{
			var line = raw.TrimEnd('\r');
			if (line.StartsWith(HeaderPrefix, StringComparison.Ordinal))
				started = line[HeaderPrefix.Length..];
			else if (line.Length > 0 && !line.StartsWith('#'))
				files.Add(line);
		}

		return (files, started);
	}
}
