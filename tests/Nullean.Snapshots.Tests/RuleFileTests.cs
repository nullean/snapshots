using System.IO.Abstractions.TestingHelpers;
using AwesomeAssertions;
using Nullean.Snapshots.Differences;

namespace Nullean.Snapshots.Tests;

/// <summary>Reading <c>.differences</c> files: the grammar, includes, and what an include may reach.</summary>
public class RuleFileTests
{
	private static string Root => TestProject.Root;

	private static MockFileSystem Repo(params (string Path, string Text)[] files)
	{
		var fs = new MockFileSystem();
		fs.AddDirectory(fs.Path.Combine(Root, ".git"));
		foreach (var (path, text) in files)
			fs.AddFile(fs.Path.Combine(Root, path.Replace('/', fs.Path.DirectorySeparatorChar)), new MockFileData(text));
		return fs;
	}

	private static string P(MockFileSystem fs, string path) => fs.Path.Combine(Root, path.Replace('/', fs.Path.DirectorySeparatorChar));

	private static RuleSet Load(MockFileSystem fs, string sidecar = "j/shop.differences") =>
		RuleLoader.Load(fs, Root, P(fs, sidecar));

	private static IReadOnlyList<string> Errors(MockFileSystem fs, string sidecar = "j/shop.differences") =>
		((Action)(() => Load(fs, sidecar))).Should().Throw<ExpectedDifferencesException>().Which.Errors;

	[Test]
	public void ReadsSectionsRulesValuesAndComments()
	{
		var fs = Repo(("j/shop.differences", """
			# why this file exists
			ignore url                      # before any section: every section

			## snapshot -c -s nav           # a heading is a glob
			added   link "What's new"
			removed link "Legacy*"
			changed heading [level=1] "Elastic Docs" -> "Elastic Documentation"
			maybe added body $..debug

			## get text #log
			changed text /9\.\d+/ -> /9\.\d+/
			ignore
			"""));

		var rules = Load(fs).Rules;

		rules.Select(r => (r.Verb, r.Maybe, r.Section)).Should().Equal(
			(RuleVerb.Ignore, false, null),
			(RuleVerb.Added, false, "snapshot -c -s nav"),
			(RuleVerb.Removed, false, "snapshot -c -s nav"),
			(RuleVerb.Changed, false, "snapshot -c -s nav"),
			(RuleVerb.Added, true, "snapshot -c -s nav"),
			(RuleVerb.Changed, false, "get text #log"),
			(RuleVerb.Ignore, false, "get text #log"));
		rules[1].Target.Select(t => t.Value).Should().Equal("link", "What's new");
		rules[3].Target.Select(t => t.Raw).Should().Equal("heading", "[level=1]");
		rules[3].Old!.Matches("Elastic Docs").Should().BeTrue();
		rules[3].New!.Matches("Elastic Documentation").Should().BeTrue();
		rules[3].New!.Matches("Elastic Docs").Should().BeFalse();
		rules[5].Old!.Token.Kind.Should().Be(RuleTokenKind.Regex);
		rules[5].Old!.Matches("v9.12").Should().BeTrue("a regular expression matches anywhere");
		rules[2].Target[1].Value.Should().Be("Legacy*");
		rules[2].Where.Line.Should().Be(6);
		rules[2].Text.Should().Be("removed link \"Legacy*\"");
	}

	[Test]
	public void GlobsMatchWholeTexts()
	{
		var rules = Load(Repo(("j/shop.differences", "## s\nchanged x \"v9.*\" -> *.0\n"))).Rules;

		rules[0].Old!.Matches("v9.1").Should().BeTrue();
		rules[0].Old!.Matches("xv9.1").Should().BeFalse();
		rules[0].New!.Matches("1.0").Should().BeTrue();
		rules[0].New!.Matches("1.01").Should().BeFalse();
	}

	[Test]
	public void ASectionHeadingKeepsItsHashTagButNotItsComment()
	{
		var rules = Load(Repo(("j/shop.differences", "## get text #log # a comment\nignore url\n"))).Rules;

		rules.Single().Section.Should().Be("get text #log");
	}

	[Test]
	public void AnEscapedQuoteAndNewlineAreRead()
	{
		var rules = Load(Repo(("j/shop.differences", "## s\nchanged text \"say \\\"hi\\\"\\nbye\" -> \"x\"\n"))).Rules;

		rules.Single().Old!.Matches("say \"hi\"\nbye").Should().BeTrue();
	}

	[Test]
	[Arguments("frobnicate x", "'frobnicate' is not a verb")]
	[Arguments("added", "a rule with no target needs a '## section'")]
	[Arguments("maybe", "'maybe' is followed by a verb")]
	[Arguments("added link \"open", "the quote at column 12 is not closed")]
	[Arguments("added link \"a\"b", "text right after the closing quote")]
	[Arguments("changed -> \"x\"", "'changed' is: changed [target] old -> new")]
	[Arguments("changed x \"a\" -> \"b\" \"c\"", "'changed' is: changed [target] old -> new")]
	[Arguments("changed x /(/ -> \"b\"", "not a regular expression")]
	[Arguments("include", "'include' takes one path")]
	[Arguments("include a b", "'include' takes one path")]
	[Arguments("includes x", "'includes' is not a verb")]
	public void ABadLineIsAnErrorWithItsLine(string line, string message)
	{
		var errors = Errors(Repo(("j/shop.differences", "# fine\n" + line + "\n")));

		errors.Should().ContainSingle().Which.Should().StartWith($"{P(new MockFileSystem(), "j/shop.differences")}:2: ").And.Contain(message);
	}

	[Test]
	public void EveryProblemIsReportedAtOnce()
	{
		var errors = Errors(Repo(("j/shop.differences", "nope\n## s\nadded\nstill nope\n")));

		errors.Should().HaveCount(2).And.Subject.Last().Should().Contain(":4:");
	}

	[Test]
	public void AnIncludeAfterASectionIsAnError() =>
		Errors(Repo(("j/shop.differences", "## s\ninclude x.differences\n"))).Should().ContainSingle().Which.Should().Contain("goes before the first '##' section");

	[Test]
	public void NoSidecarMeansNoRules()
	{
		var rules = Load(Repo());

		rules.Rules.Should().BeEmpty();
		rules.Files.Should().ContainSingle().Which.Exists.Should().BeFalse();
	}

	[Test]
	public void IncludesAreRelativeToTheFileThatIncludesThem()
	{
		var fs = Repo(
			("j/shop.differences", "include shared/nav.differences\nignore url\n"),
			("j/shared/nav.differences", "include ../common.differences\n## s\nadded link x\n"),
			("j/common.differences", "ignore text\n"));

		var rules = Load(fs);

		rules.Rules.Select(r => r.Text).Should().Equal("ignore url", "added link x", "ignore text");
		rules.Files.Select(f => f.Origin).Should().Equal(RuleOrigin.Sidecar, RuleOrigin.Shared, RuleOrigin.Shared);
		rules.Rules.Select(r => r.Origin).Should().Equal(RuleOrigin.Sidecar, RuleOrigin.Shared, RuleOrigin.Shared);
		// Depth first: a file's own rules, then those of what it includes.
	}

	[Test]
	public void ADoubleSlashIsTheGitRoot()
	{
		var fs = Repo(
			("deep/er/j/shop.differences", "include //tests/shared/nav.differences\n"),
			("tests/shared/nav.differences", "## s\nadded link x\n"));

		var rules = RuleLoader.Load(fs, Root, P(fs, "deep/er/j/shop.differences"));

		rules.Rules.Should().ContainSingle().Which.Text.Should().Be("added link x");
	}

	[Test]
	public void AMissingIncludeIsAnErrorAndAMissingOptionalOneIsNot()
	{
		var required = Repo(("j/shop.differences", "include gone.differences\n"));
		var optional = Repo(("j/shop.differences", "include? gone.differences\n## s\nignore url\n"));

		Errors(required).Should().ContainSingle().Which.Should().Contain("does not exist; use 'include?'").And.StartWith($"{P(required, "j/shop.differences")}:1:");
		var rules = Load(optional);
		rules.Rules.Should().ContainSingle();
		rules.Files.Should().Contain(f => f.Origin == RuleOrigin.Optional && !f.Exists && f.IncludedFrom!.Line == 1);
	}

	[Test]
	public void AnOptionalIncludeThatExistsIsRead()
	{
		var rules = Load(Repo(("j/shop.differences", "include? known.differences\n"), ("j/known.differences", "## s\nadded link x\n")));

		rules.Rules.Should().ContainSingle().Which.Origin.Should().Be(RuleOrigin.Optional);
	}

	[Test]
	[Arguments("include")]
	[Arguments("include?")]
	public void AnIncludeCannotLeaveTheRepository(string directive)
	{
		var fs = Repo(("j/shop.differences", directive + " ../../../outside.differences\n"));
		fs.AddFile(fs.Path.Combine(fs.Path.GetDirectoryName(Root)!, "outside.differences"), new MockFileData("## s\nignore x\n"));

		Errors(fs).Should().ContainSingle().Which.Should().Contain("is outside the repository").And.StartWith($"{P(fs, "j/shop.differences")}:1:");
	}

	[Test]
	public void ADoubleSlashCannotEscapeEither() =>
		Errors(Repo(("j/shop.differences", "include? //../outside.differences\n"))).Should().ContainSingle().Which.Should().Contain("is outside the repository");

	[Test]
	public void ASymbolicLinkIsRefused()
	{
		var fs = Repo(("j/shop.differences", "include? link.differences\n"), ("j/real.differences", "## s\nignore url\n"));
		try
		{
			fs.File.CreateSymbolicLink(P(fs, "j/link.differences"), P(fs, "j/real.differences"));
		}
		catch (Exception e) when (e is NotImplementedException or NotSupportedException or UnauthorizedAccessException)
		{
			return;
		}

		Errors(fs).Should().ContainSingle().Which.Should().MatchRegex("symlink|symbolic link");
	}

	[Test]
	public void AnIncludeLoopIsAnError()
	{
		var fs = Repo(
			("j/shop.differences", "include a.differences\n"),
			("j/a.differences", "include b.differences\n"),
			("j/b.differences", "include a.differences\n"));

		Errors(fs).Should().ContainSingle().Which.Should().Contain("includes loop").And.Contain(" -> ");
	}

	[Test]
	public void FindsTheGitRootAboveAFolder()
	{
		var fs = Repo();
		fs.AddDirectory(P(fs, "a/b/c"));

		RuleLoader.FindRepositoryRoot(fs, P(fs, "a/b/c")).Should().Be(Root);
		RuleLoader.FindRepositoryRoot(new MockFileSystem(), P(fs, "a")).Should().Be(P(fs, "a"), "without a .git the start folder is the scope");
	}

	[Test]
	public void TheSidecarIsNextToTheFile() =>
		RuleLoader.SidecarPath(P(new MockFileSystem(), "journeys/shop.journey")).Should().EndWith("shop.differences");
}
