using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Nullean.Snapshots.TUnit;

/// <summary>
/// A test data source: one case per file matching <see cref="Pattern"/> under <see cref="Folder"/> of the test
/// project, named after the file (<c>Run(basket/empty)</c>). A subfolder that another test claims with a more specific
/// folder is left to that test, so a folder needing different setup gets its own class.
/// </summary>
/// <param name="folder">The folder, relative to the test project: <c>journeys</c>.</param>
/// <param name="pattern">The files to yield: <c>*.journey</c>.</param>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Parameter, AllowMultiple = false)]
public class SnapshotFilesAttribute(string folder, string pattern) : DataSourceGeneratorAttribute<TestDataRow<SnapshotFile>>
{
	private static readonly ConcurrentDictionary<Assembly, IReadOnlyList<(string Folder, string Pattern)>> Claims = new();

	/// <summary>The folder, relative to the test project, with <c>/</c> separators.</summary>
	public string Folder { get; } = folder.Replace('\\', '/').Trim('/');

	/// <summary>The files to yield, such as <c>*.journey</c>.</summary>
	public string Pattern { get; } = pattern;

	/// <inheritdoc />
	protected override IEnumerable<Func<TestDataRow<SnapshotFile>>> GenerateDataSources(DataGeneratorMetadata dataGeneratorMetadata)
	{
		ArgumentNullException.ThrowIfNull(dataGeneratorMetadata);
		var assembly = dataGeneratorMetadata.TestInformation?.Class.Type.Assembly
			?? throw new InvalidOperationException($"[{GetType().Name}] needs a test method to find its project.");
		var files = Files(assembly, SnapshotFile.Find(assembly, Folder, Pattern));
		if (files.Count == 0)
			throw new InvalidOperationException($"No {Pattern} files under {(Folder.Length == 0 ? "the project" : Folder)}.");
		// A TestDataRow shows its data expression (the parameter's name) unless it is given a display name: keep Run(basket/empty).
		var method = dataGeneratorMetadata.TestInformation?.Name ?? "Run";
		foreach (var file in files)
		{
			var tags = file.Tags(assembly);
			yield return () => new TestDataRow<SnapshotFile>(file, DisplayName: $"{method}({file.Name})", Categories: tags.Count == 0 ? null : [.. tags]);
		}
	}

	/// <summary>Leaves out the files under a folder that another data source of the same pattern claims more specifically.</summary>
	internal IReadOnlyList<SnapshotFile> Files(Assembly assembly, IReadOnlyList<SnapshotFile> found)
	{
		var prefix = Folder.Length == 0 ? string.Empty : Folder + "/";
		var deeper = ClaimsIn(assembly)
			.Where(c => c.Pattern == Pattern && c.Folder.Length > Folder.Length && (c.Folder + "/").StartsWith(prefix, StringComparison.Ordinal))
			.Select(c => c.Folder + "/")
			.ToList();
		return [.. found.Where(f => !deeper.Any(d => f.Path.StartsWith(d, StringComparison.Ordinal)))];
	}

	[UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Test assemblies are not trimmed, and TUnit keeps every test method.")]
	[UnconditionalSuppressMessage("Trimming", "IL2070", Justification = "Test assemblies are not trimmed, and TUnit keeps every test method.")]
	[UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Test assemblies are not trimmed, and TUnit keeps every test method.")]
	private static IReadOnlyList<(string Folder, string Pattern)> ClaimsIn(Assembly assembly) =>
		Claims.GetOrAdd(assembly, static a =>
		[
			.. a.GetTypes()
				.SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
				.SelectMany(m => m.GetCustomAttributes<SnapshotFilesAttribute>(inherit: false)
					.Concat(m.GetParameters().SelectMany(p => p.GetCustomAttributes<SnapshotFilesAttribute>(inherit: false))))
				.Select(c => (c.Folder, c.Pattern))
				.Distinct(),
		]);
}

/// <summary>
/// One test case per <c>*.journey</c> file under <paramref name="folder"/> (default <c>journeys</c>), named after the
/// file. Pass the case to <c>JourneyTest.RunAsync</c>.
/// </summary>
/// <param name="folder">The folder of journeys, relative to the test project.</param>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Parameter, AllowMultiple = false)]
public sealed class JourneyFilesAttribute(string folder = "journeys") : SnapshotFilesAttribute(folder, "*.journey");

/// <summary>
/// One test case per <c>*.http</c> file under <paramref name="folder"/> (default <c>http</c>), named after the file.
/// Pass the case to <c>HttpFileTest.RunAsync</c>.
/// </summary>
/// <param name="folder">The folder of <c>.http</c> files, relative to the test project.</param>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Parameter, AllowMultiple = false)]
public sealed class HttpFilesAttribute(string folder = "http") : SnapshotFilesAttribute(folder, "*.http");
