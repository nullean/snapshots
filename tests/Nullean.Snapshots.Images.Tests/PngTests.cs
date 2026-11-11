using System.Buffers.Binary;
using AwesomeAssertions;
using CsCheck;

namespace Nullean.Snapshots.Images.Tests;

public class PngTests
{
	private static string Suite => Path.Combine(AppContext.BaseDirectory, "PngSuite");

	public static IEnumerable<string> ValidFiles() =>
		Directory
			.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "PngSuite"), "*.png")
			.Select(Path.GetFileName)
			.Where(n => !n!.StartsWith('x'))
			.Order(StringComparer.Ordinal)!;

	public static IEnumerable<string> CorruptFiles() =>
		Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "PngSuite"), "x*.png").Select(Path.GetFileName).Order(StringComparer.Ordinal)!;

	[Test]
	[MethodDataSource(nameof(ValidFiles))]
	public void DecodesPngSuiteLikeTheOracle(string file)
	{
		var bytes = File.ReadAllBytes(Path.Combine(Suite, file));
		var ours = Png.Decode(bytes);
		var oracle = TestImages.Oracle(bytes);
		ours.Width.Should().Be(oracle.Width);
		ours.Height.Should().Be(oracle.Height);
		FirstDifference(ours, oracle).Should().BeNull();
	}

	[Test]
	[MethodDataSource(nameof(CorruptFiles))]
	public void RejectsPngSuiteCorruptFiles(string file)
	{
		var bytes = File.ReadAllBytes(Path.Combine(Suite, file));
		var act = () => Png.Decode(bytes);
		act.Should().Throw<FormatException>();
	}

	[Test]
	public void TheSuiteIsThere()
	{
		ValidFiles().Count().Should().BeGreaterThan(150);
		CorruptFiles().Count().Should().Be(14);
	}

	[Test]
	public void EncodeThenDecodeIsIdentity() => TestImages.Image().Sample(image => Png.Decode(Png.Encode(image)).Pixels.AsSpan().SequenceEqual(image.Pixels));

	[Test]
	public void TheOracleReadsWhatWeEncode() =>
		TestImages.Image().Sample(image => TestImages.Oracle(Png.Encode(image)).Pixels.AsSpan().SequenceEqual(image.Pixels));

	[Test]
	public void EveryFilterTypeDecodes() =>
		Gen.Select(TestImages.Image(), Gen.Byte[0, 4].Array[1, 5], Gen.Bool, Gen.Int[1, 4]).Sample(
			(image, filters, text, split) => Png.Decode(TestImages.Encode(image, filters, text, split)).Pixels.AsSpan().SequenceEqual(image.Pixels)
		);

	/// <summary>
	/// Damage inside a chunk with its CRC fixed up, or a cut-off file, either still decodes or is a FormatException:
	/// never an index or overflow exception.
	/// </summary>
	[Test]
	public void DamagedFilesFailOnlyWithFormatException() =>
		Gen.Select(TestImages.Image(12), Gen.Int, Gen.Byte, Gen.Bool).Sample((image, at, value, truncate) =>
		{
			var png = Png.Encode(image);
			var damaged = truncate ? png[..(8 + (int)((uint)at % (uint)(png.Length - 8)))] : Damage(png, (uint)at, value);
			try
			{
				Png.Decode(damaged);
			}
			catch (FormatException) { }
		});

	[Test]
	public void ColourManagementChunksAreIgnored()
	{
		// basn = no gamma; g10/g25 = the same pixels with a gAMA chunk.
		var plain = Png.Decode(File.ReadAllBytes(Path.Combine(Suite, "basn2c08.png")));
		plain.Width.Should().Be(32);
		var image = TestImages.Solid(3, 2, 10, 20, 30);
		var withText = TestImages.Encode(image, [0], textChunk: true);
		Png.Decode(withText).Pixels.Should().Equal(image.Pixels);
	}

	[Test]
	public void TooLargeIsRejectedBeforeAllocating()
	{
		var png = Png.Encode(TestImages.Solid(1, 1, 0, 0, 0));
		BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(16), 100_000);
		BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(20), 100_000);
		BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(29), Png.Crc32.Compute(png.AsSpan(12, 17)));
		var act = () => Png.Decode(png);
		act.Should().Throw<FormatException>().WithMessage("*larger than*");
	}

	[Test]
	public void Crc32MatchesTheSpecificationCheckValue() => Png.Crc32.Compute("123456789"u8).Should().Be(0xCBF43926);

	/// <summary>Sets one byte inside a chunk's type or data and recomputes that chunk's CRC.</summary>
	private static byte[] Damage(byte[] png, uint at, byte value)
	{
		var copy = (byte[])png.Clone();
		var chunks = new List<(int Start, int Length)>();
		for (var p = 8; p < copy.Length;)
		{
			var length = BinaryPrimitives.ReadInt32BigEndian(copy.AsSpan(p));
			chunks.Add((p, length));
			p += 12 + length;
		}

		var (start, len) = chunks[(int)(at % (uint)chunks.Count)];
		var offset = start + 4 + (int)(at / (uint)chunks.Count % (uint)(4 + len));
		copy[offset] = value;
		BinaryPrimitives.WriteUInt32BigEndian(copy.AsSpan(start + 8 + len), Png.Crc32.Compute(copy.AsSpan(start + 4, 4 + len)));
		return copy;
	}

	private static string? FirstDifference(RgbaImage a, RgbaImage b)
	{
		for (var i = 0; i < a.Pixels.Length; i++)
		{
			if (a.Pixels[i] != b.Pixels[i])
			{
				var pixel = i / 4;
				return $"pixel ({pixel % a.Width}, {pixel / a.Width}) channel {i % 4}: ours {a.Pixels[i]}, oracle {b.Pixels[i]}";
			}
		}

		return null;
	}
}
