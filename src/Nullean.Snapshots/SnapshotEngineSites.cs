using System.Globalization;
using System.IO.Abstractions;
using System.Reflection;
using System.Text;
using Nullean.Snapshots.Differences;
using Nullean.Snapshots.Trees;

namespace Nullean.Snapshots;

internal sealed partial class SnapshotEngine
{
	private (SnapshotProject Project, string FullPath, string Dir, string Name, string? Variant) SiteFile(string sourceFile, string? variant)
	{
		if (variant is not null && !SnapshotPath.IsValidName(variant))
			throw new ArgumentException(
				$"The variant '{variant}' must be a non-empty name of letters, digits, '.', '-' and '_', without a leading or trailing dot.",
				nameof(variant)
			);
		var identity = (identityProvider is null ? SnapshotDefaults.TestIdentity : identityProvider())?.Current;
		var (project, fullPath, relative, name) = ResolveFile(sourceFile, identity?.TestAssembly ?? Assembly.GetEntryAssembly());
		var slash = relative.LastIndexOf('/');
		return (project, fullPath, slash < 0 ? string.Empty : relative[..slash], name, variant);
	}

	private string SitePath(SnapshotProject project, string dir, string name, string? variant, string role, string extension)
	{
		var within = SnapshotPath.KeyWithinProject(SnapshotPath.Key(project.Name, dir, string.Empty, name, variant, null));
		return SnapshotPath.File(fileSystem, project.ReceivedDirectory, within, "." + role + ".", extension);
	}

	/// <summary>Writes the baseline into the received tree; no verified file, no claim on a key, no manifest entry.</summary>
	public string SeedBaseline(string text, string extension, string sourceFile, string? variant)
	{
		var (project, _, dir, name, _) = SiteFile(sourceFile, variant);
		var path = SitePath(project, dir, name, variant, "baseline", extension);
		var fs = ScopeFor(project);
		fs.Directory.CreateDirectory(project.ReceivedDirectory);
		if (_markers.TryAdd(project, 0))
			ReceivedMarker.For(fileSystem, project).EnsureWritten(fs, project.ReceivedDirectory);
		AtomicFile.Write(fs, path, SnapshotText.Normalize(text));
		return path;
	}

	/// <summary>
	/// The folder a run against one site of a comparison writes its screenshots and HAR files into:
	/// <c>{received}/{folder}/{name}.{role}.artifacts</c>. A comparison needs one per site, so the second run does not empty
	/// the first's. <paramref name="clean"/> empties it first, as a run does; a reused baseline leaves it alone.
	/// </summary>
	public string SiteArtifactsDirectory(string sourceFile, string role, bool clean)
	{
		var (project, _, dir, name, _) = SiteFile(sourceFile, null);
		var parts = dir.Length == 0 ? [] : dir.Split('/');
		var folder = fileSystem.Path.Combine([project.ReceivedDirectory, .. parts, name + "." + role + ArtifactsSuffix]);
		if (!clean)
			return folder;
		var fs = ScopeFor(project);
		fs.Directory.CreateDirectory(project.ReceivedDirectory);
		if (_markers.TryAdd(project, 0))
			ReceivedMarker.For(fileSystem, project).EnsureWritten(fs, project.ReceivedDirectory);
		if (fs.Directory.Exists(folder))
			fs.Directory.Delete(folder, true);
		fs.Directory.CreateDirectory(folder);
		return folder;
	}

	public string? ReadBaseline(string extension, string sourceFile, string? variant)
	{
		var (project, _, dir, name, _) = SiteFile(sourceFile, variant);
		var path = SitePath(project, dir, name, variant, "baseline", extension);
		var fs = ScopeFor(project);
		return fs.File.Exists(path) ? fs.File.ReadAllText(path, SnapshotText.Encoding) : null;
	}

	public SiteComparisonResult CompareSites(
		string targetText,
		TreeSnapshotFormat format,
		RuleTargets targets,
		string sourceFile,
		SiteComparisonOptions options
	)
	{
		ArgumentNullException.ThrowIfNull(targetText);
		var (project, fullPath, dir, name, variant) = SiteFile(sourceFile, options.Variant);
		var baselinePath = SitePath(project, dir, name, variant, "baseline", format.Extension);
		var targetPath = SitePath(project, dir, name, variant, "target", format.Extension);
		var fs = ScopeFor(project);
		if (!fs.File.Exists(baselinePath))
		{
			throw new InvalidOperationException(
				$"There is no baseline for {SnapshotPath.Relative(fileSystem, project.RepositoryRoot, fullPath)}: run it against the baseline site first (at {SnapshotPath.Relative(fileSystem, project.RepositoryRoot, baselinePath)})."
			);
		}

		var rules = RuleLoader.Load(fileSystem, project.RepositoryRoot, RuleLoader.SidecarPath(fullPath));
		var bound = ExpectedDifferences.Bind(rules, targets);
		var baselineText = fs.File.ReadAllText(baselinePath, SnapshotText.Encoding);
		var target = SnapshotText.Normalize(targetText);

		Node baseline, actual;
		try
		{
			baseline = format.Parse(baselineText);
			actual = format.Parse(target);
		}
		catch (FormatException e)
		{
			return Failed(
				project,
				format,
				targets,
				fullPath,
				baselinePath,
				targetPath,
				target,
				fs,
				$"A transcript is not valid {format.Name}: {e.Message}",
				null,
				[],
				0,
				rules
			);
		}

		var differences = TreeDiffer.Instance.Diff(baseline, actual, format.Options);
		var application = ExpectedDifferences.Apply(differences, baseline, actual, bound, targets);

		IReadOnlyList<string> recorded = [];
		if (options.Record is { } recordFile && application.UnexplainedFacts.Count > 0)
		{
			recorded = ExpectedDifferences.Record(
				rules,
				recordFile,
				application.UnexplainedFacts,
				targets,
				options.Note ?? ("recorded " + DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
			);
		}

		var pruned = options.Prune is { } pruneFile ? ExpectedDifferences.Prune(rules, pruneFile, application) : 0;
		var unresolved = application.Unexplained.Count > 0 && options.Record is null;
		var staleLeft = application
			.Stale
			.Where(r => options.Prune is null || !RuleLoader.Comparer.Equals(r.Where.File, fileSystem.Path.GetFullPath(options.Prune)))
			.ToList();
		if (!unresolved && staleLeft.Count == 0)
		{
			DeleteIfExists(fs, targetPath);
			return new SiteComparisonResult(true, null, application, baselinePath, null, recorded, pruned);
		}

		return Failed(
			project,
			format,
			targets,
			fullPath,
			baselinePath,
			targetPath,
			target,
			fs,
			null,
			application,
			recorded,
			pruned,
			rules,
			staleLeft,
			unresolved
		);
	}

	private SiteComparisonResult Failed(
		SnapshotProject project,
		TreeSnapshotFormat format,
		RuleTargets targets,
		string fullPath,
		string baselinePath,
		string targetPath,
		string target,
		IFileSystem fs,
		string? invalid,
		RuleApplication? application,
		IReadOnlyList<string> recorded,
		int pruned,
		RuleSet rules,
		List<DifferenceRule>? stale = null,
		bool unresolved = true
	)
	{
		PrepareReceived(new SnapshotTarget(project, null, fs, string.Empty, string.Empty, string.Empty, targetPath));
		AtomicFile.Write(fs, targetPath, target);

		string Rel(string path) => SnapshotPath.Relative(fileSystem, project.RepositoryRoot, path);

		var message = new StringBuilder();
		message.Append(format.Name).Append(" differs between baseline and target: ").Append(Rel(fullPath)).Append('\n');
		if (invalid is not null)
			message.Append(invalid).Append('\n');
		application ??= new RuleApplication([], [], [], []);
		if (unresolved && application.Unexplained.Count > 0)
		{
			message.Append(application.Unexplained.Count.ToString(CultureInfo.InvariantCulture)).Append(
				" unexplained difference(s), baseline expected, target actual:\n"
			);
			AppendCapped(message, TreeDifferences.Describe(application.Unexplained, "baseline." + format.Extension));
		}

		if (stale is { Count: > 0 })
		{
			message.Append(stale.Count.ToString(CultureInfo.InvariantCulture)).Append(" rule(s) matched nothing, so they are stale:\n");
			foreach (var rule in stale)
				message
					.Append("  ")
					.Append(Rel(rule.Where.File))
					.Append(':')
					.Append(rule.Where.Line.ToString(CultureInfo.InvariantCulture))
					.Append(": ")
					.Append(rule.Text)
					.Append('\n');
		}

		if (unresolved && application.UnexplainedFacts.Count > 0)
		{
			message.Append("Rules that would explain them (under these headings in a file the rules include, or with --record):\n");
			foreach (var group in application.UnexplainedFacts.Where(f => f.Heading is not null).GroupBy(f => f.Heading!.ToString(), StringComparer.Ordinal))
			{
				message.Append("  ## ").Append(group.Key).Append('\n');
				foreach (var line in group.SelectMany(f => ExpectedDifferences.Suggest(f, targets)).Distinct(StringComparer.Ordinal))
					message.Append("  ").Append(line).Append('\n');
			}
		}

		message.Append("Baseline: ").Append(Rel(baselinePath)).Append('\n');
		message.Append("Target:   ").Append(Rel(targetPath)).Append('\n');
		var files = rules.Files.Where(f => f.Exists).Select(f => Rel(f.Path)).ToList();
		message.Append("Rules:    ").Append(files.Count == 0 ? Rel(RuleLoader.SidecarPath(fullPath)) + " (none yet)" : string.Join(", ", files)).Append('\n');
		return new SiteComparisonResult(false, message.ToString().TrimEnd('\n'), application, baselinePath, targetPath, recorded, pruned);
	}

	private static void AppendCapped(StringBuilder message, IReadOnlyList<IReadOnlyList<string>> blocks)
	{
		var lines = 0;
		var shown = 0;
		foreach (var block in blocks)
		{
			if (shown == SnapshotMessageLimits.MaxDifferences || (lines + block.Count > SnapshotMessageLimits.MaxLines))
				break;
			foreach (var line in block)
				message.Append(line).Append('\n');
			lines += block.Count;
			shown++;
		}

		if (blocks.Count > shown)
			message.Append("(+").Append((blocks.Count - shown).ToString(CultureInfo.InvariantCulture)).Append(" more, see the target file)\n");
	}
}
