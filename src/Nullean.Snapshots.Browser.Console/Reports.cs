using System.Globalization;
using System.Text;
using System.Xml;

namespace Nullean.Snapshots.Browser.Console;

internal static class JUnitReport
{
	public static void Write(string path, string suite, IReadOnlyList<JourneyOutcome> outcomes)
	{
		_ = Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
		using var writer = XmlWriter.Create(path, new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) });
		writer.WriteStartDocument();
		writer.WriteStartElement("testsuite");
		writer.WriteAttributeString("name", suite);
		writer.WriteAttributeString("tests", outcomes.Count.ToString(CultureInfo.InvariantCulture));
		writer.WriteAttributeString("failures", outcomes.Count(o => o.Failed).ToString(CultureInfo.InvariantCulture));
		writer.WriteAttributeString("skipped", outcomes.Count(o => o.Skipped).ToString(CultureInfo.InvariantCulture));
		writer.WriteAttributeString("time", Seconds(outcomes.Sum(o => o.Elapsed.TotalSeconds)));
		foreach (var outcome in outcomes)
		{
			var slash = outcome.File.Id.LastIndexOf('/');
			writer.WriteStartElement("testcase");
			writer.WriteAttributeString("classname", slash < 0 ? "" : outcome.File.Id[..slash]);
			writer.WriteAttributeString("name", slash < 0 ? outcome.File.Id : outcome.File.Id[(slash + 1)..]);
			writer.WriteAttributeString("time", Seconds(outcome.Elapsed.TotalSeconds));
			if (outcome.SkipReason is { } reason)
			{
				writer.WriteStartElement("skipped");
				writer.WriteAttributeString("message", reason);
				writer.WriteEndElement();
			}
			else if (outcome.Error is { } error)
			{
				writer.WriteStartElement("failure");
				writer.WriteAttributeString("message", "journey did not run");
				writer.WriteString(error);
				writer.WriteEndElement();
			}
			else if (outcome.Transcript?.Failure is { } failure)
			{
				writer.WriteStartElement("failure");
				writer.WriteAttributeString("message", failure.Command);
				writer.WriteString(failure.Describe(outcome.File.Id));
				writer.WriteEndElement();
			}
			else if (outcome.Compared is { Passed: false } compared)
			{
				writer.WriteStartElement("failure");
				writer.WriteAttributeString("message", "transcript differs");
				writer.WriteString(compared.FailureMessage ?? "");
				writer.WriteEndElement();
			}
			writer.WriteEndElement();
		}
		writer.WriteEndElement();
		writer.WriteEndDocument();
	}

	private static string Seconds(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}

internal static class StepSummary
{
	/// <summary>Appends a table of the outcomes to the file <c>GITHUB_STEP_SUMMARY</c> names, when it is set.</summary>
	public static void Write(Uri? site, IReadOnlyList<JourneyOutcome> outcomes)
	{
		if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is not { Length: > 0 } path)
			return;
		var text = new StringBuilder()
			.AppendLine(site is null ? "### Journeys" : $"### Journeys against {site}")
			.AppendLine()
			.AppendLine("| Journey | Result |")
			.AppendLine("|---|---|");
		foreach (var outcome in outcomes)
			_ = text.AppendLine(
				CultureInfo.InvariantCulture,
				$"| `{outcome.File.Id}` | {(outcome.Skipped ? $"skipped: {outcome.SkipReason}" : outcome.Passed ? "passed" : "**failed**")} |"
			);
		File.AppendAllText(path, text.ToString());
	}
}
