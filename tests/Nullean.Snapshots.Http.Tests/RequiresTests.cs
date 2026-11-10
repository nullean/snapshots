using AwesomeAssertions;
using Nullean.HttpFileParser;

namespace Nullean.Snapshots.Http.Tests;

/// <summary>Which <c>.http</c> files apply to a run: <c># @requires</c> against the run's flags, <c># @mutates</c> on a read-only run.</summary>
public class RequiresTests
{
	private static string File(string text)
	{
		var path = Path.Combine(Path.GetTempPath(), "requires-" + Guid.NewGuid().ToString("N") + ".http");
		System.IO.File.WriteAllText(path, text);
		return path;
	}

	private static HttpFileContext Context(string? buildType = null, string? env = null, params string[] features) =>
		new(new HashSet<string>(features, StringComparer.Ordinal), buildType, env);

	[Test]
	public void Unmet_FileWithoutDirectives_Applies() =>
		HttpFileSnapshot.Unmet(File("GET https://example.test/\n"), Context()).Should().BeNull();

	[Test]
	public void Unmet_EnvironmentTheRunIsIn_NamesTheConditionAsWritten()
	{
		var file = File("# @requires !env:prod\nGET https://example.test/\n");

		HttpFileSnapshot.Unmet(file, Context(env: "prod")).Should().Be("@requires !env:prod");
		HttpFileSnapshot.Unmet(file, Context(env: "edge")).Should().BeNull();
	}

	[Test]
	public void SkipReason_MutatingFileOnAReadOnlyRun_IsSkipped()
	{
		var file = File("# @mutates\nPOST https://example.test/save\n");

		HttpFileSnapshot.SkipReason(file, new HttpFileSnapshotOptions { ReadOnly = true }).Should().Be("@mutates, and the run is read-only");
		HttpFileSnapshot.SkipReason(file, new HttpFileSnapshotOptions()).Should().BeNull();
	}

	[Test]
	public void SkipReason_FlagsOfTheRunDecideRequires()
	{
		var file = File("# @requires navigation-preview\nGET https://example.test/\n");

		HttpFileSnapshot.SkipReason(file, new HttpFileSnapshotOptions()).Should().Be("@requires navigation-preview");
		HttpFileSnapshot.SkipReason(file, new HttpFileSnapshotOptions { Features = new HashSet<string> { "navigation-preview" } }).Should().BeNull();
	}

	[Test]
	public void SkipReason_UnmetRequirementComesBeforeMutates()
	{
		var file = File("# @requires env:edge\n# @mutates\nPOST https://example.test/save\n");

		HttpFileSnapshot.SkipReason(file, new HttpFileSnapshotOptions { ReadOnly = true, Env = "prod" }).Should().Be("@requires env:edge");
	}

	[Test]
	public void Metadata_ExposesTheTagsForSelection() =>
		HttpFileSnapshot.Metadata(File("# @tags mcp smoke\nGET https://example.test/\n")).Tags.Should().Equal("mcp", "smoke");
}
