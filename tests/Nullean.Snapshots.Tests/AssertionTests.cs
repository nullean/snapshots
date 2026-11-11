using AwesomeAssertions;
using AwesomeAssertions.Execution;

namespace Nullean.Snapshots.Tests;

/// <summary>
/// Runs <c>Should().MatchSnapshot()</c> against a mock project, and keeps the failure messages it produces as golden
/// snapshots of this repository, compared by the real thing.
/// </summary>
public class AssertionTests
{
	private const string Key = "Rendering/HtmlTests/RendersTable";

	[Test]
	public void MismatchMessage()
	{
		var p = new TestProject { Identity = TestProject.TUnitLike() };
		p.AddVerified(Key, "<table>\n  <tr><td>{kibana}</td></tr>\n  <tr><td>beats</td></tr>\n</table>\n");

		var message = Fail(
			p,
			() => "<table>\n  <tr><td>{Kibana}</td></tr>\n  <tr><td>beats</td></tr>\n</table>\n".Should().MatchSnapshot(
				callerFilePath: p.TestFile,
				callerMemberName: "RendersTable"
			)
		);

		message.Should().MatchSnapshot();
	}

	[Test]
	public void NewSnapshotMessage()
	{
		var p = new TestProject { Identity = TestProject.TUnitLike() };
		var message = Fail(p, () => "first {value}".Should().MatchSnapshot(callerFilePath: p.TestFile, callerMemberName: "RendersTable"));
		message.Should().MatchSnapshot();
	}

	[Test]
	public void CappedMessage()
	{
		var p = new TestProject();
		var verified = string.Join("\n", Enumerable.Range(0, 400).Select(i => $"line {i}")) + "\n";
		p.AddVerified(Key, verified);
		var actual = string.Join("\n", Enumerable.Range(0, 400).Select(i => i % 10 == 0 ? $"LINE {i}" : $"line {i}")) + "\n";

		var message = Fail(p, () => actual.Should().MatchSnapshot(callerFilePath: p.TestFile, callerMemberName: "RendersTable"));

		message.Should().MatchSnapshot();
	}

	[Test]
	public void CiMessage()
	{
		var p = new TestProject();
		p.Environment["SNAPSHOT_ACCEPT"] = "1";
		p.Environment["CI"] = "true";
		var message = Fail(p, () => "x".Should().MatchSnapshot(callerFilePath: p.TestFile, callerMemberName: "RendersTable"));
		message.Should().MatchSnapshot();
	}

	[Test]
	public void ExceptionMessageIsExactlyTheSnapshotMessage()
	{
		var p = new TestProject();
		p.AddVerified(Key, "a {0} {{1}}\n");
		var twin = new TestProject();
		twin.AddVerified(Key, "a {0} {{1}}\n");
		var direct = twin.Engine.Match("b {0} {{1}}", SnapshotFormats.Text, twin.Site(), null, null, null).FailureMessage;

		var message = Fail(p, () => "b {0} {{1}}".Should().MatchSnapshot(callerFilePath: p.TestFile, callerMemberName: "RendersTable"));

		message.Should().Be(direct).And.Contain("-a {0} {{1}}").And.Contain("+b {0} {{1}}");
	}

	[Test]
	public void AssertionScopeCollectsEveryFailure()
	{
		var p = new TestProject();
		using var engine = Use(p);
		var act = () =>
		{
			using var scope = new AssertionScope();
			"one".Should().MatchSnapshot("one", callerFilePath: p.TestFile, callerMemberName: "RendersTable");
			"two".Should().MatchSnapshot("two", callerFilePath: p.TestFile, callerMemberName: "RendersTable");
		};
		var message = act.Should().Throw<Exception>().Which.Message;
		message.Should().Contain("RendersTable.one.verified.txt").And.Contain("RendersTable.two.verified.txt");
	}

	[Test]
	public void BecauseIsShown()
	{
		var p = new TestProject();
		var message = Fail(p, () => "x".Should().MatchSnapshot(because: "rendering changed", callerFilePath: p.TestFile, callerMemberName: "RendersTable"));
		message.Should().StartWith("Text snapshot is new because rendering changed: no verified file at");
	}

	[Test]
	public void PassingAssertionDoesNotThrow()
	{
		var p = new TestProject();
		p.AddVerified(Key, "same\n");
		using var engine = Use(p);
		var act = () => "same".Should().MatchSnapshot(callerFilePath: p.TestFile, callerMemberName: "RendersTable");
		act.Should().NotThrow();
	}

	[Test]
	public void NullFails()
	{
		var p = new TestProject();
		Snapshot.EngineOverride.Value = p.Engine;
		string? value = null;
		var act = () => value.Should().MatchSnapshot(callerFilePath: p.TestFile, callerMemberName: "RendersTable");
		act.Should().Throw<Exception>().WithMessage("Expected a value to match a snapshot, but it was <null>.");
	}

	private static string Fail(TestProject project, Action assertion)
	{
		using (Use(project))
		{
			try
			{
				assertion();
			}
			catch (Exception e)
			{
				return e.Message;
			}
		}

		throw new InvalidOperationException("The assertion did not fail.");
	}

	/// <summary>
	/// Points the assertion packages at the project's engine until disposed. A synchronous test's AsyncLocal change
	/// would otherwise flow back to its caller and into later tests.
	/// </summary>
	private static EngineScope Use(TestProject project)
	{
		Snapshot.EngineOverride.Value = project.Engine;
		return new EngineScope();
	}

	private readonly struct EngineScope : IDisposable
	{
		public void Dispose() => Snapshot.EngineOverride.Value = null;
	}
}
