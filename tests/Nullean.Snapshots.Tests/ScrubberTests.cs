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
