using AwesomeAssertions;
using CsCheck;

namespace Nullean.HttpFileParser.Tests;

public class TemplateAndValidateTests
{
	// Literal text never contains "{{" and never ends in "{", so it cannot run into the reference after it.
	private static readonly Gen<string> Literal = Gen.String[Gen.Char["ab{}$ .\n\"'"], 0, 12].Where(
		s => !s.Contains("{{", StringComparison.Ordinal) && !s.EndsWith('{')
	);

	private static readonly Gen<string> Name = Gen.String[Gen.Char["abcxyz_"], 1, 6];

	[Test]
	public void SubstitutionNeverAltersTextOutsideReferences() =>
		Gen.Select(Literal, Gen.Select(Name, Literal).List[0, 5]).Sample((head, parts) =>
		{
			var text = head + string.Concat(parts.Select(p => "{{" + p.Item1 + "}}" + p.Item2));
			var template = Template.Parse(text, 1, "t.http", []);
			var rendered = template.Render(e => "<" + ((VariableExpr)e).Name.ToUpperInvariant() + ">");
			var expected = head + string.Concat(parts.Select(p => "<" + p.Item1.ToUpperInvariant() + ">" + p.Item2));
			rendered.Should().Be(expected);
		});

	[Test]
	public void SubstitutionKeepsTextWithoutReferencesVerbatim() =>
		Literal.Sample(text =>
		{
			var errors = new List<HttpFileError>();
			Template.Parse(text, 1, "t.http", errors).Render(_ => throw new InvalidOperationException()).Should().Be(text);
			errors.Should().BeEmpty();
		});

	[Test]
	public void TrimsSpacesInsideReferences() => Template.Parse("a{{ name }}b", 1, "t", []).Render(e => ((VariableExpr)e).Name).Should().Be("anameb");

	[Test]
	public void ValidateReportsVariablesDefinedLaterOrNever()
	{
		var file = HttpFile.Parse(
			"""
			@base = https://{{host}}
			GET {{base}}/a
			X-Later: {{later}}
			X-Never: {{never}}
			###
			@later = 1
			@host = example.com
			GET {{base}}/{{later}}
			"""
		);
		file
			.Validate()
			.Select(e => e.ToString())
			.Should()
			.Equal(
				"request.http:1: variable 'host' is used before its definition at line 7",
				"request.http:3: variable 'later' is used before its definition at line 6",
				"request.http:4: unknown variable 'never'"
			);
	}

	[Test]
	public void ValidateAcceptsEnvironmentAndOverrideVariables()
	{
		var file = HttpFile.Parse("GET {{host}}/{{path}}");
		file.Validate(variables: new Dictionary<string, string> { ["host"] = "h" }).Select(e => e.Message).Should().Equal("unknown variable 'path'");
		var dir = TempDir.Create();
		File.WriteAllText(Path.Combine(dir, "http-client.env.json"), /*lang=json,strict*/ """{ "dev": { "path": "p" } }""");
		var env = HttpEnvironment.Load(Path.Combine(dir, "api.http"), "dev");
		file.Validate(env, new Dictionary<string, string> { ["host"] = "h" }).Should().BeEmpty();
	}

	[Test]
	public void ValidateReportsRequestReferencesThatCannotHaveRun()
	{
		var file = HttpFile.Parse(
			"""
			GET https://example.com/{{login.response.body.$.id}}
			###
			# @name login
			POST https://example.com/login
			###
			GET https://example.com/{{login.response.headers.Location}}/{{nobody.response.body.*}}
			"""
		);
		file
			.Validate()
			.Select(e => e.ToString())
			.Should()
			.Equal(
				"request.http:1: '{{login.response.body.$.id}}' references request 'login' (line 4), which runs after this one",
				"request.http:6: '{{nobody.response.body.*}}' references a request named 'nobody', and there is none"
			);
	}

	[Test]
	public void ValidateReportsMissingBodyFiles()
	{
		var dir = TempDir.Create();
		File.WriteAllText(Path.Combine(dir, "present.json"), "{}");
		var file = HttpFile.Parse(
			"POST https://example.com\n\n< ./present.json\n###\nPOST https://example.com\n\n<@ ./missing.json",
			Path.Combine(dir, "api.http")
		);
		file.Validate().Select(e => e.Message).Should().Equal($"body file './missing.json' does not exist ({Path.Combine(dir, "missing.json")})");
	}
}
