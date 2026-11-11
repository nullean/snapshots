using AwesomeAssertions;
using Nullean.Snapshots.Tests;

namespace Nullean.Snapshots.Yaml.Tests;

/// <summary>Inline YAML snapshots: the expected YAML is a literal in the test.</summary>
public class InlineYamlTests
{
	[Test]
	public void ARealCallComparesAsATreeSoLayoutAndCommentsDoNotMatter() =>
		"name: Kibana\ntags: [ui, a]\n# a comment\n".Should().MatchInlineYamlSnapshot(
			"""
			tags:
			  - ui
			  - a
			name: Kibana
			"""
		);

	[Test]
	public void AMismatchFailsAndTheRecordKeepsTheTextAsGiven()
	{
		var p = new TestProject { Identity = TestProject.TUnitLike() };
		var message = WithEngine(
			p,
			() => "a: 1\n# kept\nb: 3\n".Should().MatchInlineYamlSnapshot(
				"a: 1\nb: 2\n",
				callerFilePath: p.TestFile,
				callerMemberName: "RendersTable",
				callerLineNumber: 10
			)
		);

		message.Should().StartWith("YAML did not match inline snapshot: tests/Acme.Tests/Rendering/HtmlTests.cs:10");
		InlineRecord.Parse(p.Read(p.ReceivedFile("Rendering/HtmlTests/RendersTable.L10", "inline"))).Edit.NewValue.Should().Be("a: 1\n# kept\nb: 3\n");
	}

	[Test]
	public void InvalidYamlFailsBeforeAnythingIsWritten()
	{
		var p = new TestProject { Identity = TestProject.TUnitLike() };
		var message = WithEngine(
			p,
			() => "a: 1\na: 2\n".Should().MatchInlineYamlSnapshot("a: 1", callerFilePath: p.TestFile, callerMemberName: "RendersTable", callerLineNumber: 10)
		);

		message.Should().StartWith("Expected valid YAML");
		p.Fs.AllFiles.Should().NotContain(f => f.EndsWith(".received.inline", StringComparison.Ordinal));
	}

	[Test]
	public void ANewSnapshotFails()
	{
		var p = new TestProject { Identity = TestProject.TUnitLike() };
		WithEngine(p, () => "a: 1".Should().MatchInlineYamlSnapshot(callerFilePath: p.TestFile, callerMemberName: "RendersTable", callerLineNumber: 10))
			.Should()
			.StartWith("YAML inline snapshot is new: MatchInlineYamlSnapshot()");
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
