using AwesomeAssertions;
using Nullean.Snapshots.Tests;

namespace Nullean.Snapshots.Html.Tests;

public class HtmlAssertionTests
{
	[Test]
	public void BeHtmlPassesForTheSameStructure()
	{
		var act =
			() => "<div><p class='a' id='b'>hi</p></div>".Should().BeHtml(
				"""
			<div>
			  <p id="b" class="a">hi</p>
			</div>
			"""
			);
		act.Should().NotThrow();
	}

	[Test]
	public void BeHtmlFailureMessage()
	{
		var message = Message(
			() => "<p class=\"b\">{Kibana}</p><ul><li>one</li></ul>".Should().BeHtml(
				"<p class=\"a\">{kibana}</p><ul><li>one</li><li>two</li></ul>",
				because: "the list must not shrink"
			)
		);
		message.Should().MatchSnapshot();
	}

	[Test]
	public void BeHtmlAppliesOptionsToBothSides()
	{
		var options = new HtmlSnapshotOptions().Select("main").RemoveAttributes("hx-*");
		var act = () => "<nav>menu</nav><main><a hx-get=x href=y>l</a></main>".Should().BeHtml("<main><a href=y hx-swap=z>l</a></main>", options);
		act.Should().NotThrow();
	}

	[Test]
	public void ContainHtmlFindsNestedFragments()
	{
		var act = () => "<section><div><p>Rendered <strong>output</strong></p></div></section>".Should().ContainHtml("<p>Rendered <strong>output</strong></p>");
		act.Should().NotThrow();
	}

	// Port of docs-builder's PrettyHtmlExtensionsTests.ShouldContainHtml_WhenExpectedHtmlIsMissing_Throws.
	[Test]
	public void ContainHtmlFailsWhenTheFragmentIsMissing()
	{
		var message = Message(() => "<p>Rendered output</p>".Should().ContainHtml("<strong>Missing output</strong>"));
		message.Should().Be("Expected HTML to contain:\n<strong>Missing output</strong>\nbut it was not found in:\n<p>Rendered output</p>");
	}

	[Test]
	public void NotContainHtml()
	{
		var pass = () => "<p>a</p>".Should().NotContainHtml("<b>a</b>");
		pass.Should().NotThrow();
		Message(() => "<div><b>a</b></div>".Should().NotContainHtml("<b>a</b>"))
			.Should()
			.StartWith("Expected HTML not to contain:\n<b>a</b>\nbut it was found in:\n<div>");
	}

	[Test]
	public void NullIsAFailure() => Message(() => ((string?)null).Should().BeHtml("<p/>")).Should().Be("Expected HTML, but it was <null>.");

	[Test]
	public void SnapshotIsStoredPrettyAndComparedStructurally()
	{
		var p = new TestProject { Identity = TestProject.TUnitLike() };
		Snapshot.EngineOverride.Value = p.Engine;
		try
		{
			var first =
				() => "<div><p>a</p></div>".Should().MatchHtmlSnapshot(callerFilePath: p.TestFile, callerMemberName: "RendersTable", callerLineNumber: 1);
			first.Should().Throw<Exception>().WithMessage("HTML snapshot is new*");
			var received = p.ReceivedFile("Rendering/HtmlTests/RendersTable", "html");
			p.Read(received).Should().Be("<div>\n\t<p>a</p>\n</div>\n");

			var verified = p.VerifiedFile("Rendering/HtmlTests/RendersTable", "html");
			p.Fs.Directory.CreateDirectory(p.Fs.Path.GetDirectoryName(verified)!);
			p.Fs.File.Move(received, verified);
			var whitespaceOnly =
				() => "<div>\n   <p>a</p></div>".Should().MatchHtmlSnapshot(callerFilePath: p.TestFile, callerMemberName: "RendersTable", callerLineNumber: 1);
			whitespaceOnly.Should().NotThrow();
		}
		finally
		{
			Snapshot.EngineOverride.Value = null;
		}
	}

	[Test]
	public void SnapshotMismatchMessage()
	{
		var p = new TestProject { Identity = TestProject.TUnitLike() };
		p.Fs.AddFile(p.VerifiedFile("Rendering/HtmlTests/RendersTable", "html"), new(HtmlSnapshot.Pretty("<table><tr><td>{kibana}</td></tr></table>")));
		Snapshot.EngineOverride.Value = p.Engine;
		string message;
		try
		{
			message = Message(
				() => "<table><tr><td class=x>{Kibana}</td></tr></table>".Should().MatchHtmlSnapshot(
					callerFilePath: p.TestFile,
					callerMemberName: "RendersTable"
				)
			);
		}
		finally
		{
			Snapshot.EngineOverride.Value = null;
		}

		message.Should().MatchSnapshot();
	}

	private static string Message(Action assertion)
	{
		try
		{
			assertion();
		}
		catch (Exception e)
		{
			return e.Message;
		}

		throw new InvalidOperationException("The assertion did not fail.");
	}
}
