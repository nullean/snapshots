using AwesomeAssertions;
using CsCheck;

namespace Nullean.Snapshots.Tests;

public partial class ScrubberTests
{
	private static readonly Gen<string> Volatile = Gen.OneOf(
		Gen.Guid.Select(g => g.ToString()),
		Gen.Guid.Select(g => g.ToString("B").ToUpperInvariant()),
		Gen.DateTime.Select(d => d.ToString("O", System.Globalization.CultureInfo.InvariantCulture)),
		Gen.DateTime.Select(d => d.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)),
		Gen.String[Gen.Char["abc019-:T {}"], 0, 8]
	);

	private static readonly Gen<string> Text = Volatile.Array[0, 8].Select(parts => string.Join(" ", parts));

	// #39: paths.

	[Test]
	[Arguments("/work/repo", "see /work/repo/docs/a.md here", "see {repo}/docs/a.md here")]
	[Arguments("/work/repo/", "/work/repo", "{repo}")]
	[Arguments("/work/repo", "\"/work/repo/a\", '/work/repo/b'", "\"{repo}/a\", '{repo}/b'")]
	[Arguments("/work/repo", "/work/repository/a /var/work/repo/a x/work/repo", "/work/repository/a /var/work/repo/a x/work/repo")]
	[Arguments("C:\\work\\repo", "C:\\work\\repo\\docs\\a.md failed", "{repo}/docs/a.md failed")]
	[Arguments("C:/work/repo", "C:\\work\\repo\\docs\\a.md", "{repo}/docs/a.md")]
	[Arguments("/work/repo", "{\"path\":\"/work/repo/a/b\"}", "{\"path\":\"{repo}/a/b\"}")]
	public void PathReplacesTheRootAndNormalisesTheRest(string root, string text, string expected) =>
		Scrubbers.Path(root, "{repo}")(text).Should().Be(expected);

	[Test]
	public void PathMatchesTheJsonEscapedWindowsForm() =>
		Scrubbers.Path("C:\\work\\repo", "{repo}")("{\"file\":\"C:\\\\work\\\\repo\\\\docs\\\\a.md\"}").Should().Be("{\"file\":\"{repo}/docs/a.md\"}");

	[Test]
	public void PathRejectsAnEmptyRoot()
	{
		var act = () => Scrubbers.Path("/", "x");
		act.Should().Throw<ArgumentException>();
	}

	[Test]
	public void TempDirectoryScrubsTheTempFolder()
	{
		var temp = System.IO.Path.GetTempPath().TrimEnd(System.IO.Path.DirectorySeparatorChar);
		var file = System.IO.Path.Combine(temp, "a", "b.txt");

		Scrubbers.TempDirectory()($"wrote {file} ok").Should().Be("wrote {temp}/a/b.txt ok");
	}

	[Test]
	public void PathIsIdempotent() =>
		Gen.String[Gen.Char["ab/\\ ."], 0, 20].Sample(t => Scrubbers.Path("/a/b", "{r}")(Scrubbers.Path("/a/b", "{r}")(t)) == Scrubbers.Path("/a/b", "{r}")(t));

	[Test]
	public void GuidsAreIdempotent() => Text.Sample(t => Scrubbers.Guids(Scrubbers.Guids(t)) == Scrubbers.Guids(t));

	[Test]
	public void DateTimesAreIdempotent() => Text.Sample(t => Scrubbers.DateTimes(Scrubbers.DateTimes(t)) == Scrubbers.DateTimes(t));

	[Test]
	public void ScrubbedTextHasNoGuidsLeft() => Text.Sample(t => !AGuid().IsMatch(Scrubbers.Guids(t)));

	[System.Text.RegularExpressions.GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
	private static partial System.Text.RegularExpressions.Regex AGuid();

	[Test]
	public void SameValueGetsTheSameNumber()
	{
		var a = Guid.NewGuid();
		var b = Guid.NewGuid();
		Scrubbers.Guids($"{a} {b} {a.ToString().ToUpperInvariant()}").Should().Be("Guid_1 Guid_2 Guid_1");
	}

	[Test]
	[Arguments("at 2026-11-03T08:53:10Z.", "at DateTime_1.")]
	[Arguments("2026-11-03T08:53:10.1234567+01:00", "DateTime_1")]
	[Arguments("2026-11-03 08:53", "DateTime_1")]
	[Arguments("version 2026-11-03", "version 2026-11-03")]
	public void DateTimes(string input, string expected) => Scrubbers.DateTimes(input).Should().Be(expected);
}
