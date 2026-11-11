using AwesomeAssertions;
using CsCheck;

namespace Nullean.Snapshots.Tests;

public class SnapshotTextTests
{
	[Test]
	[Arguments("", "")]
	[Arguments("\n\n", "")]
	[Arguments("a", "a\n")]
	[Arguments("a\r\nb\rc\n\n\n", "a\nb\nc\n")]
	[Arguments("\uFEFFa", "a\n")]
	[Arguments("\uFEFF\uFEFFa", "a\n")]
	public void Normalizes(string input, string expected) => SnapshotText.Normalize(input).Should().Be(expected);

	[Test]
	public void NormalizeIsIdempotent() =>
		Gen.String[Gen.Char["a\r\n\uFEFF{}"], 0, 30].Sample(s =>
		{
			var once = SnapshotText.Normalize(s);
			return SnapshotText.Normalize(once) == once && !once.Contains('\r', StringComparison.Ordinal);
		});
}
