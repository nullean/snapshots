using AwesomeAssertions;
using Nullean.Snapshots.Tests;
using Nullean.Snapshots.Trees;

namespace Nullean.Snapshots.Json.Tests;

/// <summary>Inline JSON snapshots: the expected JSON is a literal in the test.</summary>
public class InlineJsonTests
{
	[Test]
	public void ARealCallComparesWithItsRawLiteralAsATree() =>
		/*lang=json,strict*/
		"""{"name":"Kibana","tags":["ui","a"],"id":2}""".Should().MatchInlineJsonSnapshot(/*lang=json,strict*/
			"""
			{
			  "id": 2.0,
			  "name": "Kibana",
			  "tags": ["ui", "a"]
			}
			"""
		);

	[Test]
	public void ARealObjectCallSerializesWithTheTypeInfo() =>
		new Product(2, "Kibana", ["ui"])
			.Should()
			.MatchInlineJsonSnapshot(
				TestJsonContext.Default.Product, /*lang=json,strict*/
				"""
			{"Id": 2, "Name": "Kibana", "Tags": ["ui"]}
			"""
			);

	[Test]
	public void AMismatchFailsWithThePathAndWritesPrettyJsonToTheRecord()
	{
		var p = new TestProject { Identity = TestProject.TUnitLike() };
		var message = WithEngine(
			p,
			() => /*lang=json,strict*/ """{"id":1,"tags":["a","c"]}""".Should().MatchInlineJsonSnapshot(/*lang=json,strict*/
				"""{"id":1,"tags":["a","b"]}""",
				callerFilePath: p.TestFile,
				callerMemberName: "RendersTable",
				callerLineNumber: 10
			)
		);

		message.Should().StartWith("JSON did not match inline snapshot: tests/Acme.Tests/Rendering/HtmlTests.cs:10\n1 difference(s):\n$.tags[1]");
		var record = InlineRecord.Parse(p.Read(p.ReceivedFile("Rendering/HtmlTests/RendersTable.L10", "inline")));
		record.Edit.MethodName.Should().Be("MatchInlineJsonSnapshot");
		record.Edit.NewValue.Should().Be(/*lang=json,strict*/ "{\n  \"id\": 1,\n  \"tags\": [\n    \"a\",\n    \"c\"\n  ]\n}\n");
		record.Format.Should().Be("JSON");
	}

	[Test]
	public void ANewSnapshotIsWrittenPretty()
	{
		var p = new TestProject { Identity = TestProject.TUnitLike() };
		var message = WithEngine(
			p,
			() => new Product(1, "a", [])
				.Should()
				.MatchInlineJsonSnapshot(TestJsonContext.Default.Product, callerFilePath: p.TestFile, callerMemberName: "RendersTable", callerLineNumber: 10)
		);

		message.Should().StartWith(
			"JSON inline snapshot is new: MatchInlineJsonSnapshot() at tests/Acme.Tests/Rendering/HtmlTests.cs:10 has no expected value"
		);
		InlineRecord
			.Parse(p.Read(p.ReceivedFile("Rendering/HtmlTests/RendersTable.L10", "inline")))
			.Edit
			.NewValue
			.Should()
			.Be(/*lang=json,strict*/ "{\n  \"Id\": 1,\n  \"Name\": \"a\",\n  \"Tags\": []\n}\n");
	}

	[Test]
	public void IgnorePathsApplyToInlineSnapshots()
	{
		var p = new TestProject { Identity = TestProject.TUnitLike() };
		var message = WithEngine(
			p,
			() => /*lang=json,strict*/ """{"id":1,"at":"2026-11-03"}""".Should().MatchInlineJsonSnapshot(
				/*lang=json,strict*/
				"""{"id":1,"at":"2026-01-01"}""",
				new TreeDiffOptions { IgnorePaths = ["$.at"] },
				callerFilePath: p.TestFile,
				callerMemberName: "RendersTable",
				callerLineNumber: 10
			)
		);
		message.Should().BeNull();
	}

	[Test]
	public void NullIsAFailure()
	{
		var p = new TestProject { Identity = TestProject.TUnitLike() };
		WithEngine(p, () => ((string?)null).Should().MatchInlineJsonSnapshot("{}", callerFilePath: p.TestFile))
			.Should()
			.Be("Expected JSON, but it was <null>.");
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
