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
	/// <summary>Appends the summary of the run to <paramref name="path"/>, when there is one.</summary>
	public static void Write(string? path, Uri? site, IReadOnlyList<JourneyOutcome> outcomes, string? rerun)
	{
		if (path is not { Length: > 0 })
			return;
		try
		{
			File.AppendAllText(path, GitHubActions.Summary(site, outcomes, rerun));
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			// A summary is for the reader of the CI page: failing to add to it must not fail the run.
		}
	}
}
