using AwesomeAssertions;
using CsCheck;

namespace Nullean.Snapshots.Tests;

public class ForbiddenValuesTests
{
	private const string Key = "Rendering/HtmlTests/RendersTable";

	[Test]
	public void AForbiddenValueFailsWithoutNamingItAndWritesNoReceivedFile()
	{
		var p = new TestProject();

		var result = p.Match("ok\nthe key is hunter2 here\nok\nhunter2 again", options: new SnapshotOptions().Forbid(["hunter2"]));

		result.Passed.Should().BeFalse();
		result.FailureMessage.Should().Contain("holds forbidden values").And.Contain("2 lines (2, 4)").And.NotContain("hunter2");
		p.Fs.File.Exists(p.ReceivedFile(Key)).Should().BeFalse();
	}

	[Test]
	public void AValueTheScrubberRemovesIsNotForbidden()
	{
		var p = new TestProject();
		p.AddVerified(Key, "key is Secret_1\n");

		var options = new SnapshotOptions().Scrub(new System.Text.RegularExpressions.Regex("hunter2"), "Secret_1").Forbid(["hunter2"]);

		p.Match("key is hunter2", options: options).Passed.Should().BeTrue();
	}

	[Test]
	public void AForbiddenValueAlreadyInTheVerifiedFileFails()
	{
		var p = new TestProject();
		p.AddVerified(Key, "key is hunter2\n");

		var result = p.Match("key is Secret_1", options: new SnapshotOptions().Forbid(["hunter2"]));

		result.Passed.Should().BeFalse();
		result.FailureMessage.Should().Contain("In the verified file: 1 line (1)").And.NotContain("In the value").And.NotContain("hunter2");
	}

	[Test]
	public void AcceptingDoesNotGetAroundIt()
	{
		var p = new TestProject();
		p.Environment["SNAPSHOT_ACCEPT"] = "1";

		var result = p.Match("key is hunter2", options: new SnapshotOptions().Forbid(["hunter2"]));

		result.Passed.Should().BeFalse();
		p.Fs.File.Exists(p.VerifiedFile(Key)).Should().BeFalse();
	}

	[Test]
	public void AnInlineSnapshotIsGuardedToo()
	{
		var p = new TestProject();

		var result = p.MatchInline("key is hunter2", "key is hunter2", options: new SnapshotOptions().Forbid(["hunter2"]));

		result.Passed.Should().BeFalse();
		result.FailureMessage.Should().Contain("In the value").And.Contain("In the expected literal").And.NotContain("hunter2");
	}

	[Test]
	public void EmptyValuesAreIgnoredAndOtherTextPasses()
	{
		var p = new TestProject();
		p.AddVerified(Key, "fine\n");

		p.Match("fine", options: new SnapshotOptions().Forbid(["", "hunter2"])).Passed.Should().BeTrue();
	}

	[Test]
	public void TheDefaultListAppliesToEverySnapshotEvenWhenDefaultScrubbersAreIgnored()
	{
		SnapshotDefaults.Forbid(["zz-forbidden-by-default"]);
		try
		{
			var result = new TestProject().Match("has zz-forbidden-by-default", options: new SnapshotOptions { IgnoreDefaultScrubbers = true });

			result.Passed.Should().BeFalse();
			result.FailureMessage.Should().Contain("holds forbidden values").And.NotContain("zz-forbidden-by-default");
		}
		finally
		{
			SnapshotDefaults.ClearForbidden();
		}
	}

	[Test]
	public void LinesReportsExactlyTheLinesThatContainAValue() =>
		Gen.String[Gen.Char["ab\n"], 0, 30].Sample(text =>
		{
			var expected = text.Split('\n').Select((line, i) => (line, n: i + 1)).Where(x => x.line.Contains("ab", StringComparison.Ordinal)).Select(x => x.n);
			return ForbiddenValues.Lines(text, ["ab"]).SequenceEqual(expected);
		});
}
