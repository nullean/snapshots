using System.Globalization;
using Nullean.PublicApi;
using Nullean.PublicApi.Diff;

namespace Nullean.Snapshots.PublicApi;

/// <summary>Writes an assembly's public API as a <c>.api</c> file, and compares two of them.</summary>
public static class PublicApiSnapshot
{
	/// <summary>The default format: changes to an extension point count as breaking for its implementers.</summary>
	public static SnapshotFormat Format { get; } = new PublicApiSnapshotFormat(null);

	/// <summary>A format that compares with <paramref name="options"/>, such as types that are not extension points.</summary>
	public static SnapshotFormat FormatWith(ApiComparisonOptions? options) => options is null ? Format : new PublicApiSnapshotFormat(options);

	/// <summary>The <c>.api</c> text of the assembly at <paramref name="path"/>.</summary>
	public static string Write(string path) => ApiWriter.Write(ApiReader.Read(path));

	/// <summary>The <c>.api</c> files of the assembly at <paramref name="path"/>, split as asked.</summary>
	public static IReadOnlyList<ApiFile> Write(string path, ApiSplit split) => ApiWriter.Write(ApiReader.Read(path), split);

	/// <summary>Compares two <c>.api</c> texts.</summary>
	public static SnapshotComparison Compare(string verified, string actual, ApiComparisonOptions? options = null) =>
		FormatWith(options).Compare(verified, actual);
}

internal sealed class PublicApiSnapshotFormat(ApiComparisonOptions? options) : SnapshotFormat
{
	public override string Name => "public API";

	public override string Extension => "api";

	public override SnapshotComparison Compare(string verified, string actual)
	{
		if (string.Equals(verified, actual, StringComparison.Ordinal))
			return SnapshotComparison.Match;

		ApiDocument expected;
		try
		{
			expected = ApiParser.Parse([("verified.api", verified)]);
		}
		catch (ApiParseException e)
		{
			return new SnapshotComparison(false, [[$"The verified file is not a valid .api file: {e.Message}"]]);
		}

		var result = ApiComparison.Compare(expected.Assembly, ApiParser.Parse(actual).Assembly, options);
		// the same API written differently, such as by another version of the writer: show the text
		if (result.Changes.Count == 0)
			return new SnapshotComparison(
				false,
				LineDiff.Hunks(verified, actual, LineDiff.DefaultContext),
				Summary: "The API is the same, written differently:"
			);

		var breaking = result.Breaking.Count();
		var summary = $"Suggested version change: {result.Suggest()}" + (breaking == 0 ? "" : $" ({breaking.ToString(CultureInfo.InvariantCulture)} breaking)");
		return new SnapshotComparison(false, [.. result.Changes.Select(c => (IReadOnlyList<string>)[Line(c, expected)])], Summary: summary);
	}

	/// <summary>
	/// One change: where, what, the version change it asks for and who it breaks, and where in the verified file:
	/// <c>Lib › Widget › Draw()  removed void Draw()  [major: breaks callers, compiled code and source]  (verified.api:9)</c>.
	/// </summary>
	private static string Line(ApiChange change, ApiDocument expected)
	{
		var where = change.Where + (change.Part.Length > 0 ? " › " + change.Part : "");
		var what = change.Kind switch
		{
			ApiChangeKind.Added => "added " + change.New,
			ApiChangeKind.Removed => "removed " + change.Old,
			_ => $"{change.Old ?? "-"} → {change.New ?? "-"}",
		};
		var verdict = change.Level.ToString().ToLowerInvariant();
		if (change.Breaks != ApiAudience.None)
			verdict += $": breaks {Audience(change.Breaks)}, {Compatibility(change.Compatibility)}{(change.Counts ? "" : ", which does not count")}";
		var location = (change.OldNode is null ? null : expected.LocationOf(change.OldNode)) ??
			(change.OldScope is null ? null : expected.LocationOf(change.OldScope));
		return $"{where}  {what}  [{verdict}]" + (change.Note is null ? "" : $" ({change.Note})") +
			(location is null ? "" : $"   ({location.File}:{location.Line.ToString(CultureInfo.InvariantCulture)})");
	}

	private static string Audience(ApiAudience audience) =>
		string.Join(
			" and ",
			new[] { (ApiAudience.Callers, "callers"), (ApiAudience.Implementers, "implementers"), (ApiAudience.Inheritors, "inheritors") }.Where(
				a => (audience & a.Item1) != 0
			).Select(a => a.Item2)
		);

	private static string Compatibility(ApiCompatibility compatibility) => compatibility switch
	{
		ApiCompatibility.Binary => "compiled code only",
		ApiCompatibility.Source => "source only",
		_ => "compiled code and source",
	};
}
