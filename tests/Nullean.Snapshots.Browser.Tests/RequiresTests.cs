using AwesomeAssertions;
using Nullean.Cdp.Journeys;

namespace Nullean.Snapshots.Browser.Tests;

/// <summary>
/// Which journeys apply to a run (<c>@requires</c>, <c>@mutates</c>), and that the run's flags and defaults reach the
/// runner. The first group needs no browser; the second runs journeys in Chrome.
/// </summary>
public class RequiresTests
{
	private static string Journey(string text)
	{
		var path = Path.Combine(Path.GetTempPath(), "requires-" + Guid.NewGuid().ToString("N") + ".journey");
		File.WriteAllText(path, text);
		return path;
	}

	private static JourneyContext Context(string? buildType = null, string? env = null, params string[] features) =>
		new(new HashSet<string>(features, StringComparer.Ordinal), buildType, env);

	[Test]
	public void Unmet_JourneyWithoutDirectives_Applies()
	{
		var file = Journey("open /\n");

		JourneySnapshot.Unmet(file, Context()).Should().BeNull();
	}

	[Test]
	public void Unmet_FlagTheRunLacks_NamesTheConditionAsWritten()
	{
		var file = Journey("# @requires navigation-preview\nopen /\n");

		JourneySnapshot.Unmet(file, Context()).Should().Be("@requires navigation-preview");
		JourneySnapshot.Unmet(file, Context(features: "navigation-preview")).Should().BeNull();
	}

	[Test]
	public void Unmet_NegatedEnvironment_HoldsWhenUnknownAndFailsOnTheNamedOne()
	{
		var file = Journey("# @requires !env:prod build-type:assembler,codex\nopen /\n");

		JourneySnapshot.Unmet(file, Context("assembler")).Should().BeNull();
		JourneySnapshot.Unmet(file, Context("assembler", "prod")).Should().Be("@requires !env:prod");
		JourneySnapshot.Unmet(file, Context("isolated")).Should().Be("@requires build-type:assembler,codex");
	}

	[Test]
	public void SkipReason_MutatingJourneyOnAReadOnlyRun_IsSkipped()
	{
		var file = Journey("# @mutates\nopen /\n");

		JourneySnapshot.SkipReason(file, new JourneySnapshotOptions { ReadOnly = true }).Should().Be("@mutates, and the run is read-only");
		JourneySnapshot.SkipReason(file, new JourneySnapshotOptions()).Should().BeNull();
	}

	[Test]
	public void SkipReason_UnmetRequirementComesBeforeMutates()
	{
		var file = Journey("# @requires navigation-preview\n# @mutates\nopen /\n");

		JourneySnapshot.SkipReason(file, new JourneySnapshotOptions { ReadOnly = true }).Should().Be("@requires navigation-preview");
	}

	[Test]
	public void Metadata_ExposesTheTagsForSelection()
	{
		var file = Journey("# @tags navigation smoke\nopen /\n");

		JourneySnapshot.Metadata(file).Tags.Should().Equal("navigation", "smoke");
	}

	[Test]
	public async Task RunAsync_FeatureBlockTheRunDoesNotMeet_IsOneSkippedLineInTheTranscript()
	{
		LiveJourneyTests.RequireChrome();
		await using var server = new RunOptionsServer();
		var file = Journey("open /\nfeature navigation-preview\n  click #nothing-here\nend\nget title\n");

		var skipped = await JourneySnapshot.RunAsync(file, new JourneySnapshotOptions { BaseUrl = new Uri(server.Url) });
		var run = await JourneySnapshot.RunAsync(
			file,
			new JourneySnapshotOptions { BaseUrl = new Uri(server.Url), Features = new HashSet<string> { "navigation-preview" } }
		);

		skipped.Failure.Should().BeNull();
		skipped.Text.Should().Contain("skipped: navigation-preview");
		run.Failure.Should().NotBeNull("the block runs when the flag is on, and its step has nothing to click");
	}

	[Test]
	public async Task RunAsync_UserAgentAndHeaders_ReachThePage()
	{
		LiveJourneyTests.RequireChrome();
		await using var server = new RunOptionsServer();
		var file = Journey("open /\n");

		var transcript = await JourneySnapshot.RunAsync(
			file,
			new JourneySnapshotOptions
			{
				BaseUrl = new Uri(server.Url),
				UserAgent = "Elastic/Synthetics requires-test",
				Headers = new Dictionary<string, string> { ["X-Run"] = "monitor" },
			}
		);

		transcript.Failure.Should().BeNull();
		server.UserAgents.Should().Contain("Elastic/Synthetics requires-test");
		server.RunHeaders.Should().Contain("monitor");
	}

	[Test]
	public async Task RunAsync_ReadOnly_RefusesTheWriteAndAWritableUrlLetsItThrough()
	{
		LiveJourneyTests.RequireChrome();
		await using var server = new RunOptionsServer();
		var file = Journey("open /\nclick #save\nwait 300\n");

		var refused = await JourneySnapshot.RunAsync(file, new JourneySnapshotOptions { BaseUrl = new Uri(server.Url), ReadOnly = true });
		server.Methods.Should().NotContain("POST /save");
		var allowed = await JourneySnapshot.RunAsync(
			file,
			new JourneySnapshotOptions { BaseUrl = new Uri(server.Url), ReadOnly = true, WritableUrls = [server.Url + "save"], }
		);

		refused.Failure.Should().NotBeNull();
		allowed.Failure.Should().BeNull();
		server.Methods.Should().Contain("POST /save");
	}

	[Test]
	public async Task RunAsync_Viewport_IsTheSizeTheJourneyStartsWith()
	{
		LiveJourneyTests.RequireChrome();
		await using var server = new RunOptionsServer();
		var file = Journey("open /\nget box html\n");

		var transcript = await JourneySnapshot.RunAsync(
			file,
			new JourneySnapshotOptions { BaseUrl = new Uri(server.Url), Viewport = new JourneyViewport(777, 555) }
		);

		transcript.Text.Should().Contain("777");
	}
}
