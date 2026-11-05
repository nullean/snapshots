using AwesomeAssertions;
using Nullean.Snapshots.Tests;

namespace Nullean.Snapshots.Html.Tests;

/// <summary>Inline HTML snapshots: the expected HTML is a literal in the test.</summary>
public class InlineHtmlTests
{
	[Test]
	public void ARealCallComparesStructurallySoWhitespaceDoesNotMatter() =>
		"<div>\n   <p>a</p></div>".Should().MatchInlineHtmlSnapshot("""
			<div>
				<p>a</p>
			</div>
			""");

	[Test]
	public void AMismatchFailsAndTheRecordHoldsThePrettyPrintedHtml()
	{
		var p = new TestProject { Identity = TestProject.TUnitLike() };
		var message = WithEngine(p, () => "<div><p class=x>a</p></div>".Should()
			.MatchInlineHtmlSnapshot("<div><p>a</p></div>", callerFilePath: p.TestFile, callerMemberName: "RendersTable", callerLineNumber: 10));

		message.Should().StartWith("HTML did not match inline snapshot: tests/Acme.Tests/Rendering/HtmlTests.cs:10");
		InlineRecord.Parse(p.Read(p.ReceivedFile("Rendering/HtmlTests/RendersTable.L10", "inline"))).Edit.NewValue
			.Should().Be("<div>\n\t<p class=\"x\">a</p>\n</div>\n");
	}

	[Test]
	public void ASelectedFragmentIsWhatIsComparedAndStored()
	{
		var p = new TestProject { Identity = TestProject.TUnitLike() };
		var message = WithEngine(p, () => "<main><h1>t</h1><p>a</p></main>".Should().MatchInlineHtmlSnapshot(
			html: new HtmlSnapshotOptions().Remove("h1"),
			callerFilePath: p.TestFile,
			callerMemberName: "RendersTable",
			callerLineNumber: 10));

		message.Should().StartWith("HTML inline snapshot is new");
		InlineRecord.Parse(p.Read(p.ReceivedFile("Rendering/HtmlTests/RendersTable.L10", "inline"))).Edit.NewValue
			.Should().Be("<main>\n\t<p>a</p>\n</main>\n");
	}

	private static string? WithEngine(TestProject project, Action assertion)
	{
		Snapshot.EngineOverride.Value = project.Engine;
		try
		{
			assertion();
			return null;
		}
		catch (Exception e)
		{
			return e.Message;
		}
		finally
		{
			Snapshot.EngineOverride.Value = null;
		}
	}
}
