using AwesomeAssertions;
using Nullean.Cdp.Journeys;
using Nullean.HttpFileParser;

namespace Nullean.Snapshots.Browser.Tests;

/// <summary>
/// A <c>.journey</c> and a <c>.http</c> file take the same header directives. The two parsers are separate (the http one
/// has no dependency on Nullean.Cdp), so this parses the same text with both and fails when they drift apart.
/// </summary>
public class HeaderGrammarConformanceTests
{
	private static JourneyMetadata Journey(string header) => Cdp.Journeys.Journey.Parse(header + "\nopen /\n").Metadata;

	private static HttpFileMetadata Http(string header) => HttpFile.Parse(header + "\nGET https://example.test/\n").Metadata;

	[Test]
	[Arguments("# @tags navigation smoke")]
	[Arguments("# @tags a.b c_d e-f")]
	[Arguments("# @requires navigation-preview")]
	[Arguments("# @requires !navigation-preview !env:prod build-type:assembler,codex")]
	[Arguments("# @requires env:edge,demo")]
	[Arguments("# @monitor")]
	[Arguments("# @monitor schedule=10")]
	[Arguments("# @monitor schedule=10 locations=germany,us_east")]
	[Arguments("# @alert pagerduty slack")]
	[Arguments("# @mutates")]
	[Arguments("# @tags one\n# @tags two two\n# @requires a\n# @requires b\n# @alert x\n# @alert x y")]
	public void SameDirectives_GiveTheSameMetadata(string header)
	{
		var journey = Journey(header);
		var http = Http(header);

		http.Tags.Should().Equal(journey.Tags);
		http.Requires.Select(r => r.ToString()).Should().Equal(journey.Requires.Select(r => r.ToString()));
		http.Requires.Select(r => (r.Kind, string.Join(",", r.Values), r.Negated)).Should().Equal(journey.Requires.Select(r => (r.Kind, string.Join(",", r.Values), r.Negated)));
		http.Monitor?.Schedule.Should().Be(journey.Monitor?.Schedule);
		(http.Monitor?.Locations ?? []).Should().Equal(journey.Monitor?.Locations ?? []);
		(http.Monitor is null).Should().Be(journey.Monitor is null);
		http.Alerts.Should().Equal(journey.Alerts);
		http.Mutates.Should().Be(journey.Mutates);
	}

	[Test]
	[Arguments("# @tags Bad")]
	[Arguments("# @tags")]
	[Arguments("# @requires")]
	[Arguments("# @requires Bad")]
	[Arguments("# @requires env:")]
	[Arguments("# @requires env:Prod")]
	[Arguments("# @requires host:a")]
	[Arguments("# @monitor schedule=x")]
	[Arguments("# @monitor schedule=0")]
	[Arguments("# @monitor locations=")]
	[Arguments("# @monitor locations=Bad")]
	[Arguments("# @monitor speed=1")]
	[Arguments("# @monitor schedule=1\n# @monitor schedule=2")]
	[Arguments("# @alert Bad")]
	[Arguments("# @alert")]
	[Arguments("# @mutates x")]
	public void SameMistakes_GiveTheSameMessages(string header)
	{
		var journeyErrors = Assert.Throws<JourneyParseException>(() => Journey(header)).Errors.Select(e => e.Message).ToList();
		var httpErrors = Assert.Throws<HttpFileParseException>(() => Http(header)).Errors.Select(e => e.Message).ToList();

		httpErrors.Should().Equal(journeyErrors);
	}

	[Test]
	[Arguments("navigation-preview", "assembler", "prod", "navigation-preview")]
	[Arguments("navigation-preview", "assembler", "prod", "")]
	[Arguments("!env:prod", "assembler", "prod", "")]
	[Arguments("!env:prod", "assembler", null, "")]
	[Arguments("build-type:assembler,codex", "codex", null, "")]
	[Arguments("build-type:assembler,codex", null, "edge", "")]
	[Arguments("env:edge,demo", "assembler", "DEMO", "")]
	public void SameRequirement_IsMetByTheSameContexts(string requires, string? buildType, string? env, string features)
	{
		var flags = features.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
		var header = $"# @requires {requires}";

		var journeyUnmet = Journey(header).Unmet(new JourneyContext(flags, buildType, env));
		var httpUnmet = Http(header).Unmet(new HttpFileContext(flags, buildType, env));

		httpUnmet?.ToString().Should().Be(journeyUnmet?.ToString());
	}
}
