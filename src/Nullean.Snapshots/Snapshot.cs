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
	private static readonly SnapshotEngine Engine = new(new FileSystem(), Environment.GetEnvironmentVariable);

	/// <summary>Lets a test run the assertion packages against its own engine, without touching other tests.</summary>
	internal static AsyncLocal<SnapshotEngine?> EngineOverride { get; } = new();

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
}

/// <summary>The snapshot workflow over an <see cref="IFileSystem"/>, so tests can run it on a mock.</summary>
internal sealed class SnapshotEngine(
	IFileSystem fileSystem,
	Func<string, string?> getVariable,
	Func<ISnapshotTestIdentityProvider?>? identityProvider = null,
	Func<SnapshotCallSite, Assembly?, SnapshotProject>? projectResolver = null
)
{
	private readonly SnapshotProjectResolver _resolver = new(fileSystem);
	private readonly ConcurrentDictionary<string, string> _owners = new(StringComparer.OrdinalIgnoreCase);
	private readonly ConcurrentDictionary<SnapshotProject, IFileSystem> _scoped = new();

	public SnapshotResult Match(
		string actual,
		SnapshotFormat format,
		SnapshotCallSite callSite,
		string? name,
		SnapshotOptions? options,
		string? because
	)
	{
		ArgumentNullException.ThrowIfNull(actual);
		ArgumentNullException.ThrowIfNull(format);
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
			identity is null ? null : SnapshotPath.ArgumentsHash(identity.Arguments));
		var withinProject = SnapshotPath.KeyWithinProject(key);
		var verifiedPath = SnapshotPath.File(fileSystem, project.SnapshotsDirectory, withinProject, SnapshotPath.VerifiedInfix, format.Extension);
		var receivedPath = SnapshotPath.File(fileSystem, project.ReceivedDirectory, withinProject, SnapshotPath.ReceivedInfix, format.Extension);

		ClaimKey(key, identity, callSite);

		var fs = ScopeFor(project);
		var value = SnapshotText.Normalize(Scrub(actual, options));
		var verified = fs.File.Exists(verifiedPath) ? SnapshotText.Normalize(fs.File.ReadAllText(verifiedPath, SnapshotText.Encoding)) : null;
		var comparison = verified is null ? null : format.Compare(verified, value);

		if (comparison is { IsMatch: true })
		{
			DeleteIfExists(fs, receivedPath);
			return new SnapshotResult(true, null, key, verifiedPath, receivedPath);
		}

		var environment = SnapshotEnvironment.From(getVariable);
		if (environment.Mode == SnapshotMode.Accept)
		{
			AtomicFile.Write(fs, verifiedPath, value);
			DeleteIfExists(fs, receivedPath);
			return new SnapshotResult(true, null, key, verifiedPath, receivedPath);
		}

		fs.Directory.CreateDirectory(project.ReceivedDirectory);
		ReceivedMarker.For(fileSystem, project).EnsureWritten(fs, project.ReceivedDirectory);
		AtomicFile.Write(fs, receivedPath, value);

		var failure = new SnapshotFailure(
			format.Name,
			SnapshotPath.Relative(fileSystem, project.RepositoryRoot, verifiedPath),
			SnapshotPath.Relative(fileSystem, project.RepositoryRoot, receivedPath),
			key,
			comparison,
			Because(because),
			project.RelativeDirectory(fileSystem),
			identity?.TreeNodeFilter,
			environment.AcceptIgnoredInCi);
		return new SnapshotResult(false, failure.Render(), key, verifiedPath, receivedPath);
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
		new Nullean.ScopedFileSystem.ScopedFileSystem(fileSystem, new ScopedFileSystemOptions(project.SnapshotsDirectory, project.ReceivedDirectory)
		{
			AllowedHiddenFolderNames = [".snapshots", ".artifacts"],
			AllowedHiddenFileNames = [ReceivedMarker.FileName],
		});
}
