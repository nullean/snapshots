using System.Buffers.Binary;
using System.IO.Compression;
using CsCheck;

namespace Nullean.Snapshots.Images.Tests;

/// <summary>Images and PNG files for tests, written independently of the encoder under test.</summary>
internal static class TestImages
{
	public static RgbaImage Solid(int width, int height, byte r, byte g, byte b, byte a = 255)
	{
		var pixels = new byte[width * height * 4];
		for (var i = 0; i < pixels.Length; i += 4)
		{
			pixels[i] = r;
			pixels[i + 1] = g;
			pixels[i + 2] = b;
			pixels[i + 3] = a;
		}

		return new RgbaImage(width, height, pixels);
	}

	public static RgbaImage With(this RgbaImage image, int x, int y, byte r, byte g, byte b, byte a = 255)
	{
		var pixels = (byte[])image.Pixels.Clone();
		var i = ((y * image.Width) + x) * 4;
		pixels[i] = r;
		pixels[i + 1] = g;
		pixels[i + 2] = b;
		pixels[i + 3] = a;
		return image with { Pixels = pixels };
	}

	public static Gen<RgbaImage> Image(int maxSide = 24) =>
		Gen.Select(Gen.Int[1, maxSide], Gen.Int[1, maxSide]).SelectMany((w, h) => Gen.Byte.Array[w * h * 4].Select(p => new RgbaImage(w, h, p)));

	/// <summary>
	/// An 8-bit RGBA PNG with the given filter type on each row (cycled), an optional ancillary chunk and the image data
	/// split into several IDAT chunks: the same pixels as any other encoding, different bytes.
	/// </summary>
	public static byte[] Encode(RgbaImage image, IReadOnlyList<byte> filters, bool textChunk = false, int idatSplit = 1)
	{
		var stride = image.Width * 4;
		var raw = new byte[(stride + 1) * image.Height];
		for (var y = 0; y < image.Height; y++)
		{
			var filter = filters[y % filters.Count];
			raw[y * (stride + 1)] = filter;
			for (var i = 0; i < stride; i++)
			{
				int x = image.Pixels[(y * stride) + i];
				int a = i >= 4 ? image.Pixels[(y * stride) + i - 4] : 0;
				int b = y > 0 ? image.Pixels[((y - 1) * stride) + i] : 0;
				int c = i >= 4 && y > 0 ? image.Pixels[((y - 1) * stride) + i - 4] : 0;
				var predicted = filter switch
				{
					0 => 0,
					1 => a,
					2 => b,
					3 => (a + b) / 2,
					_ => Paeth(a, b, c),
				};
				raw[(y * (stride + 1)) + 1 + i] = (byte)(x - predicted);
			}
		}

		using var deflated = new MemoryStream();
		using (var z = new ZLibStream(deflated, CompressionLevel.Fastest, leaveOpen: true))
			z.Write(raw);
		var data = deflated.ToArray();

		using var png = new MemoryStream();
		png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
		var ihdr = new byte[13];
		BinaryPrimitives.WriteInt32BigEndian(ihdr, image.Width);
		BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), image.Height);
		ihdr[8] = 8;
		ihdr[9] = 6;
		Chunk(png, "IHDR", ihdr);
		if (textChunk)
			Chunk(png, "tEXt", "Software\0another encoder"u8.ToArray());
		var size = Math.Max(1, (data.Length + idatSplit - 1) / idatSplit);
		for (var at = 0; at < data.Length; at += size)
			Chunk(png, "IDAT", data.AsSpan(at, Math.Min(size, data.Length - at)).ToArray());
		Chunk(png, "IEND", []);
		return png.ToArray();
	}

	private static int Paeth(int a, int b, int c)
	{
		var p = a + b - c;
		int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
		return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
	}

	public static void Chunk(Stream png, string type, byte[] data)
	{
		var buffer = new byte[12 + data.Length];
		BinaryPrimitives.WriteInt32BigEndian(buffer, data.Length);
		System.Text.Encoding.ASCII.GetBytes(type, buffer.AsSpan(4));
		data.CopyTo(buffer, 8);
		BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(8 + data.Length), Png.Crc32.Compute(buffer.AsSpan(4, 4 + data.Length)));
		png.Write(buffer);
	}

	/// <summary>Decodes with StbImageSharp, the oracle.</summary>
	public static RgbaImage Oracle(byte[] png)
	{
		var result = StbImageSharp.ImageResult.FromMemory(png, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
		return new RgbaImage(result.Width, result.Height, result.Data);
	}
}
