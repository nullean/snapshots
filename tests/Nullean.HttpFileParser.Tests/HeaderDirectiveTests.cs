using AwesomeAssertions;

namespace Nullean.HttpFileParser.Tests;

/// <summary>The header directives (<c># @tags</c>, <c>@requires</c>, <c>@monitor</c>, <c>@alert</c>, <c>@mutates</c>) in the first block.</summary>
public class HeaderDirectiveTests
{
	private static HttpFileContext Context(string? buildType = null, string? env = null, params string[] features) =>
		new(new HashSet<string>(features, StringComparer.Ordinal), buildType, env);

	private static HttpFileParseException Parse(string text) =>
		Assert.Throws<HttpFileParseException>(() => HttpFile.Parse(text));

	[Test]
	public void Metadata_FileWithoutDirectives_IsEmpty()
	{
		var file = HttpFile.Parse("GET https://example.test/\n");

		file.Metadata.Should().BeSameAs(HttpFileMetadata.None);
		file.Metadata.IsEmpty.Should().BeTrue();
	}

	[Test]
	public void Metadata_AllDirectives_AreParsed()
	{
		var file = HttpFile.Parse("""
			# @tags mcp smoke
			# @requires navigation-preview !env:prod build-type:assembler,codex
			# @monitor schedule=10 locations=germany,us_east
			# @alert pagerduty
			# @mutates
			@host = https://example.test

			GET {{host}}/
			""");

		var m = file.Metadata;
		m.Tags.Should().Equal("mcp", "smoke");
		m.Requires.Select(r => r.ToString()).Should().Equal("navigation-preview", "!env:prod", "build-type:assembler,codex");
		m.Monitor!.Schedule.Should().Be(10);
		m.Monitor.Locations.Should().Equal("germany", "us_east");
		m.Alerts.Should().Equal("pagerduty");
		m.Mutates.Should().BeTrue();
		file.Requests.Should().ContainSingle();
	}

	[Test]
	public void Metadata_HeaderInABlockOfItsOwn_DoesNotMakeItARequestlessError()
	{
		var file = HttpFile.Parse("""
			# @tags mcp
			# @requires !env:prod

			### health
			GET https://example.test/health
			""");

		file.Metadata.Tags.Should().Equal("mcp");
		file.Requests.Should().ContainSingle().Which.Name.Should().Be("health");
	}

	[Test]
	public void Metadata_PerRequestDirectivesStillWork()
	{
		var file = HttpFile.Parse("""
			# @tags mcp

			### one
			# @readonly
			POST https://example.test/mcp
			""");

		file.Requests[0].Directives.ReadOnly.Should().BeTrue();
		file.Metadata.Tags.Should().Equal("mcp");
	}

	[Test]
	public void Unmet_FlagTheRunLacks_NamesTheConditionAsWritten()
	{
		var m = HttpFile.Parse("# @requires navigation-preview\nGET https://example.test/\n").Metadata;

		m.Unmet(Context()).Should().NotBeNull().And.Subject.ToString().Should().Be("navigation-preview");
		m.Unmet(Context(features: "navigation-preview")).Should().BeNull();
	}

	[Test]
	public void Unmet_UnknownContext_IsNotMatched()
	{
		var m = HttpFile.Parse("# @requires !env:prod build-type:assembler\nGET https://example.test/\n").Metadata;

		m.Unmet(Context("assembler")).Should().BeNull("an unknown environment is not prod, so the negated condition holds");
		m.Unmet(Context("assembler", "prod")).Should().NotBeNull().And.Subject.ToString().Should().Be("!env:prod");
		m.Unmet(Context(env: "edge")).Should().NotBeNull().And.Subject.ToString().Should().Be("build-type:assembler", "an unknown build type matches no plain condition");
	}

	[Test]
	[Arguments("# @tags Bad_Tag", 1, "'Bad_Tag' is not a tag name: use lower case letters, digits, '.', '_' or '-'")]
	[Arguments("# @tags", 1, "@tags needs a value: @tags <name...>, e.g. @tags navigation smoke")]
	[Arguments("# @requires env:Prod", 1, "'env:Prod' needs one or more values of lower case letters, digits, '_' or '-', separated by commas")]
	[Arguments("# @requires host:x", 1, "'host:x' is not a condition: 'host:' is not one of build-type: or env:")]
	[Arguments("# @monitor schedule=0", 1, "'schedule=0' needs a number of minutes: schedule=10")]
	[Arguments("# @monitor speed=3", 1, "unknown @monitor option 'speed=3': use schedule=<minutes> or locations=<a,b>")]
	[Arguments("# @alert Page", 1, "'Page' is not an alert channel: use lower case letters, digits and '-'")]
	[Arguments("# @mutates yes", 1, "usage: @mutates, with nothing after it")]
	public void Directive_WithAProblem_IsAnErrorOnItsLine(string directive, int line, string message)
	{
		var error = Parse(directive + "\nGET https://example.test/\n").Errors.Should().ContainSingle().Which;

		error.Line.Should().Be(line);
		error.Message.Should().Be(message);
	}

	[Test]
	public void Monitor_Twice_IsAnError()
	{
		var errors = Parse("# @monitor schedule=5\n# @monitor schedule=6\nGET https://example.test/\n").Errors;

		errors.Should().ContainSingle().Which.Message.Should().Be("@monitor may appear once");
	}

	[Test]
	public void Directive_InALaterBlock_BelongsInTheHeader()
	{
		var errors = Parse("""
			GET https://example.test/one

			###
			# @requires !env:prod
			GET https://example.test/two
			""").Errors;

		errors.Should().ContainSingle().Which.Should().Match<HttpFileError>(e => e.Line == 4 && e.Message.StartsWith("@requires belongs in the header", StringComparison.Ordinal));
	}

	[Test]
	public void OtherAtWords_AreStillPlainComments()
	{
		var file = HttpFile.Parse("# @note keep this\n# @no-log\nGET https://example.test/\n");

		file.Metadata.IsEmpty.Should().BeTrue();
		file.Requests.Should().ContainSingle();
	}
}
