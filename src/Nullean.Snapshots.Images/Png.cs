using System.Buffers.Binary;
using System.IO.Compression;

namespace Nullean.Snapshots.Images;

/// <summary>An image as 8-bit RGBA, not premultiplied, rows top to bottom.</summary>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
/// <param name="Pixels"><c>Width * Height * 4</c> bytes: red, green, blue, alpha.</param>
internal sealed record RgbaImage(int Width, int Height, byte[] Pixels)
{
	public int PixelCount => Width * Height;
}

/// <summary>
/// A PNG decoder and encoder in managed integer code, so every machine decodes a file to the same RGBA. Decodes every
/// colour type, bit depth and interlacing the specification allows, with <c>tRNS</c> transparency; ignores colour
/// management (<c>gAMA</c>, <c>cHRM</c>, <c>sRGB</c>, <c>iCCP</c>) and all other ancillary chunks, so two files with
/// equal pixels decode equally whatever wrote them. Encodes 8-bit RGBA only, for received diff images.
/// </summary>
internal static class Png
{
	private static ReadOnlySpan<byte> Signature => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

	/// <summary>
	/// The largest image decoded, in pixels: 8192 × 8192. Far beyond a screenshot, and it keeps 16-bit RGBA data within
	/// one array.
	/// </summary>
	public const long MaxPixels = 8192L * 8192L;

	private const uint Ihdr = 0x49484452, Plte = 0x504C5445, Trns = 0x74524E53, Idat = 0x49444154, Iend = 0x49454E44;

	public static bool HasSignature(ReadOnlySpan<byte> data) => data.StartsWith(Signature);

	/// <summary>Decodes a PNG to RGBA. Throws <see cref="FormatException"/> for anything that is not a valid PNG.</summary>
	public static RgbaImage Decode(ReadOnlySpan<byte> data)
	{
		if (!HasSignature(data))
			throw new FormatException("Not a PNG: the file does not start with the PNG signature.");

		Header? header = null;
		ReadOnlySpan<byte> palette = default;
		ReadOnlySpan<byte> transparency = default;
		using var compressed = new MemoryStream();
		var position = Signature.Length;
		var sawIdat = false;
		var idatEnded = false;
		var sawEnd = false;

		while (!sawEnd)
		{
			if (data.Length - position < 12)
				throw new FormatException("Truncated PNG: a chunk is cut off.");
			var length = BinaryPrimitives.ReadUInt32BigEndian(data[position..]);
			if (length > int.MaxValue || length > (uint)(data.Length - position - 12))
				throw new FormatException("Truncated PNG: a chunk is longer than the file.");
			var typeAndData = data.Slice(position + 4, 4 + (int)length);
			var type = BinaryPrimitives.ReadUInt32BigEndian(typeAndData);
			var chunk = typeAndData[4..];
			var crc = BinaryPrimitives.ReadUInt32BigEndian(data[(position + 8 + (int)length)..]);
			if (Crc32.Compute(typeAndData) != crc)
				throw new FormatException($"Corrupt PNG: the {TypeName(type)} chunk fails its CRC.");
			position += 12 + (int)length;

			if (header is null && type != Ihdr)
				throw new FormatException("Invalid PNG: the first chunk is not IHDR.");
			if (sawIdat && type != Idat)
				idatEnded = true;

			switch (type)
			{
				case Ihdr when header is null:
					header = Header.Read(chunk);
					break;
				case Ihdr:
					throw new FormatException("Invalid PNG: more than one IHDR chunk.");
				case Plte:
					if (chunk.Length == 0 || chunk.Length % 3 != 0 || chunk.Length > 256 * 3)
						throw new FormatException("Invalid PNG: the PLTE chunk is not 1 to 256 RGB entries.");
					palette = chunk;
					break;
				case Trns:
					transparency = chunk;
					break;
				case Idat when idatEnded:
					throw new FormatException("Invalid PNG: IDAT chunks are not consecutive.");
				case Idat:
					sawIdat = true;
					compressed.Write(chunk);
					break;
				case Iend:
					sawEnd = true;
					break;
				default:
					// Bit 5 of the first byte clear marks a critical chunk, which a decoder must understand.
					if ((type & 0x20000000) == 0)
						throw new FormatException($"Unsupported PNG: unknown critical chunk {TypeName(type)}.");
					break;
			}
		}

		var h = header!.Value;
		if (!sawIdat)
			throw new FormatException("Invalid PNG: no IDAT chunk.");
		if (h.ColorType == 3 && palette.IsEmpty)
			throw new FormatException("Invalid PNG: a palette image without a PLTE chunk.");
		if (h.ColorType == 3 && palette.Length / 3 > 1 << h.BitDepth)
			throw new FormatException("Invalid PNG: the palette has more entries than the bit depth allows.");

		var raw = Inflate(compressed, h.RawLength());
		return Expand(h, raw, palette, transparency);
	}

	/// <summary>Encodes RGBA as an 8-bit RGBA PNG, every row with the Up filter.</summary>
	public static byte[] Encode(RgbaImage image)
	{
		var stride = image.Width * 4;
		var filtered = new byte[(stride + 1) * image.Height];
		for (var y = 0; y < image.Height; y++)
		{
			var row = image.Pixels.AsSpan(y * stride, stride);
			var output = filtered.AsSpan(y * (stride + 1), stride + 1);
			if (y == 0)
			{
				output[0] = 0;
				row.CopyTo(output[1..]);
				continue;
			}

			output[0] = 2;
			var above = image.Pixels.AsSpan((y - 1) * stride, stride);
			for (var i = 0; i < stride; i++)
				output[1 + i] = (byte)(row[i] - above[i]);
		}

		using var deflated = new MemoryStream();
		using (var zlib = new ZLibStream(deflated, CompressionLevel.Optimal, leaveOpen: true))
			zlib.Write(filtered);

		using var png = new MemoryStream();
		png.Write(Signature);
		Span<byte> ihdr = stackalloc byte[13];
		BinaryPrimitives.WriteInt32BigEndian(ihdr, image.Width);
		BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], image.Height);
		ihdr[8] = 8; // bit depth
		ihdr[9] = 6; // RGBA
		ihdr[10] = ihdr[11] = ihdr[12] = 0; // deflate, adaptive filtering, no interlace
		WriteChunk(png, Ihdr, ihdr);
		WriteChunk(png, Idat, deflated.GetBuffer().AsSpan(0, (int)deflated.Length));
		WriteChunk(png, Iend, []);
		return png.ToArray();
	}

	private static void WriteChunk(MemoryStream png, uint type, ReadOnlySpan<byte> data)
	{
		var buffer = new byte[12 + data.Length];
		BinaryPrimitives.WriteInt32BigEndian(buffer, data.Length);
		BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(4), type);
		data.CopyTo(buffer.AsSpan(8));
		BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(8 + data.Length), Crc32.Compute(buffer.AsSpan(4, 4 + data.Length)));
		png.Write(buffer);
	}

	private static string TypeName(uint type)
	{
		Span<char> name = stackalloc char[4];
		for (var i = 0; i < 4; i++)
		{
			var c = (char)((type >> (24 - (8 * i))) & 0xFF);
			name[i] = char.IsAsciiLetter(c) ? c : '?';
		}

		return new string(name);
	}

	private static byte[] Inflate(MemoryStream compressed, long expected)
	{
		var raw = new byte[expected];
		compressed.Position = 0;
		try
		{
			using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
			var read = 0;
			while (read < raw.Length)
			{
				var n = zlib.Read(raw, read, raw.Length - read);
				if (n == 0)
					throw new FormatException("Truncated PNG: the image data ends early.");
				read += n;
			}
		}
		catch (Exception e) when (e is InvalidDataException or IOException)
		{
			// Some corrupt streams surface as InvalidDataException, others as the zlib layer's ZLibException (an IOException): both mean
			// the same, and a damaged file must only ever fail with FormatException.
			throw new FormatException("Corrupt PNG: the image data does not decompress.", e);
		}

		return raw;
	}

	/// <summary>The seven Adam7 passes: start column, start row, column step, row step.</summary>
	private static ReadOnlySpan<byte> Adam7 => [0, 0, 8, 8, 4, 0, 8, 8, 0, 4, 4, 8, 2, 0, 4, 4, 0, 2, 2, 4, 1, 0, 2, 2, 0, 1, 1, 2];

	private static RgbaImage Expand(Header h, byte[] raw, ReadOnlySpan<byte> palette, ReadOnlySpan<byte> transparency)
	{
		var pixels = new byte[(long)h.Width * h.Height * 4];
		var image = new RgbaImage(h.Width, h.Height, pixels);
		var expander = new Expander(h, palette, transparency);
		if (h.Interlace == 0)
		{
			Unfilter(h, raw, h.Width, h.Height);
			expander.Pass(raw, pixels, h.Width, h.Height, 0, 0, 1, 1, image.Width);
			return image;
		}

		var offset = 0;
		for (var pass = 0; pass < 7; pass++)
		{
			int x0 = Adam7[pass * 4], y0 = Adam7[(pass * 4) + 1], dx = Adam7[(pass * 4) + 2], dy = Adam7[(pass * 4) + 3];
			var w = (h.Width - x0 + dx - 1) / dx;
			var ht = (h.Height - y0 + dy - 1) / dy;
			if (w <= 0 || ht <= 0)
				continue;
			var length = (int)h.PassLength(w, ht);
			var data = raw.AsSpan(offset, length).ToArray();
			Unfilter(h, data, w, ht);
			expander.Pass(data, pixels, w, ht, x0, y0, dx, dy, image.Width);
			offset += length;
		}

		return image;
	}

	/// <summary>Undoes the scanline filters in place. Each row is a filter byte followed by the filtered bytes.</summary>
	private static void Unfilter(Header h, byte[] data, int width, int height)
	{
		var stride = h.Stride(width);
		var bpp = h.BytesPerPixel;
		for (var y = 0; y < height; y++)
		{
			var start = y * (stride + 1);
			var filter = data[start];
			var row = data.AsSpan(start + 1, stride);
			var above = y == 0 ? [] : data.AsSpan(start - stride, stride);
			switch (filter)
			{
				case 0:
					break;
				case 1:
					for (var i = bpp; i < stride; i++)
						row[i] += row[i - bpp];
					break;
				case 2:
					if (y > 0)
					{
						for (var i = 0; i < stride; i++)
							row[i] += above[i];
					}

					break;
				case 3:
					for (var i = 0; i < stride; i++)
					{
						var left = i >= bpp ? row[i - bpp] : 0;
						var up = y > 0 ? above[i] : 0;
						row[i] += (byte)((left + up) >> 1);
					}

					break;
				case 4:
					for (var i = 0; i < stride; i++)
					{
						var left = i >= bpp ? row[i - bpp] : 0;
						var up = y > 0 ? above[i] : 0;
						var upLeft = i >= bpp && y > 0 ? above[i - bpp] : 0;
						row[i] += Paeth(left, up, upLeft);
					}

					break;
				default:
					throw new FormatException($"Corrupt PNG: unknown filter type {filter} on a row.");
			}
		}
	}

	private static byte Paeth(int a, int b, int c)
	{
		var p = a + b - c;
		var pa = Math.Abs(p - a);
		var pb = Math.Abs(p - b);
		var pc = Math.Abs(p - c);
		return (byte)(pa <= pb && pa <= pc ? a : pb <= pc ? b : c);
	}

	private readonly record struct Header(int Width, int Height, int BitDepth, int ColorType, int Interlace)
	{
		public int Channels => ColorType switch
		{
			0 => 1,
			2 => 3,
			3 => 1,
			4 => 2,
			_ => 4
		};

		public int BitsPerPixel => Channels * BitDepth;

		/// <summary>The filter's byte distance: whole bytes per pixel, at least one.</summary>
		public int BytesPerPixel => Math.Max(1, BitsPerPixel / 8);

		public int Stride(int width) => (int)((((long)width * BitsPerPixel) + 7) / 8);

		public long PassLength(int width, int height) => (Stride(width) + 1L) * height;

		public long RawLength()
		{
			if (Interlace == 0)
				return PassLength(Width, Height);
			long total = 0;
			for (var pass = 0; pass < 7; pass++)
			{
				int x0 = Adam7[pass * 4], y0 = Adam7[(pass * 4) + 1], dx = Adam7[(pass * 4) + 2], dy = Adam7[(pass * 4) + 3];
				var w = (Width - x0 + dx - 1) / dx;
				var h = (Height - y0 + dy - 1) / dy;
				if (w > 0 && h > 0)
					total += PassLength(w, h);
			}

			return total;
		}

		public static Header Read(ReadOnlySpan<byte> chunk)
		{
			if (chunk.Length != 13)
				throw new FormatException("Invalid PNG: IHDR is not 13 bytes.");
			var width = BinaryPrimitives.ReadUInt32BigEndian(chunk);
			var height = BinaryPrimitives.ReadUInt32BigEndian(chunk[4..]);
			if (width == 0 || height == 0 || width > int.MaxValue || height > int.MaxValue)
				throw new FormatException("Invalid PNG: width and height must be between 1 and 2^31-1.");
			if ((long)width * height > MaxPixels)
				throw new FormatException($"Unsupported PNG: {width}×{height} is larger than {MaxPixels} pixels.");
			int depth = chunk[8], color = chunk[9];
			var valid = color switch
			{
				0 => depth is 1 or 2 or 4 or 8 or 16,
				3 => depth is 1 or 2 or 4 or 8,
				2 or 4 or 6 => depth is 8 or 16,
				_ => false,
			};
			if (!valid)
				throw new FormatException($"Invalid PNG: colour type {color} with bit depth {depth}.");
			if (chunk[10] != 0 || chunk[11] != 0)
				throw new FormatException("Unsupported PNG: unknown compression or filter method.");
			if (chunk[12] > 1)
				throw new FormatException("Unsupported PNG: unknown interlace method.");
			return new Header((int)width, (int)height, depth, color, chunk[12]);
		}
	}

	/// <summary>Turns unfiltered rows of any colour type and depth into RGBA8.</summary>
	private sealed class Expander
	{
		private readonly Header _h;
		private readonly byte[] _palette = new byte[256 * 4];
		private readonly int _paletteEntries;
		private readonly int _transparentGray = -1;
		private readonly int _transparentR = -1, _transparentG = -1, _transparentB = -1;

		public Expander(Header h, ReadOnlySpan<byte> palette, ReadOnlySpan<byte> transparency)
		{
			_h = h;
			switch (h.ColorType)
			{
				case 3:
					_paletteEntries = palette.Length / 3;
					for (var i = 0; i < _paletteEntries; i++)
					{
						_palette[i * 4] = palette[i * 3];
						_palette[(i * 4) + 1] = palette[(i * 3) + 1];
						_palette[(i * 4) + 2] = palette[(i * 3) + 2];
						_palette[(i * 4) + 3] = i < transparency.Length ? transparency[i] : (byte)255;
					}

					if (transparency.Length > palette.Length / 3)
						throw new FormatException("Invalid PNG: tRNS has more entries than the palette.");
					break;
				case 0 when !transparency.IsEmpty:
					if (transparency.Length != 2)
						throw new FormatException("Invalid PNG: tRNS for a greyscale image is not 2 bytes.");
					_transparentGray = BinaryPrimitives.ReadUInt16BigEndian(transparency);
					break;
				case 2 when !transparency.IsEmpty:
					if (transparency.Length != 6)
						throw new FormatException("Invalid PNG: tRNS for an RGB image is not 6 bytes.");
					_transparentR = BinaryPrimitives.ReadUInt16BigEndian(transparency);
					_transparentG = BinaryPrimitives.ReadUInt16BigEndian(transparency[2..]);
					_transparentB = BinaryPrimitives.ReadUInt16BigEndian(transparency[4..]);
					break;
				case 4 or 6 when !transparency.IsEmpty:
					throw new FormatException("Invalid PNG: tRNS on an image that has an alpha channel.");
			}
		}

		/// <summary>Writes one (sub-)image of <paramref name="width"/> × <paramref name="height"/> into the pixel grid.</summary>
		public void Pass(byte[] data, byte[] pixels, int width, int height, int x0, int y0, int dx, int dy, int imageWidth)
		{
			var stride = _h.Stride(width);
			for (var y = 0; y < height; y++)
			{
				var row = data.AsSpan((y * (stride + 1)) + 1, stride);
				var outY = y0 + (y * dy);
				for (var x = 0; x < width; x++)
				{
					var target = pixels.AsSpan(checked((int)(((outY * (long)imageWidth) + x0 + (x * dx)) * 4)), 4);
					Pixel(row, x, target);
				}
			}
		}

		private void Pixel(ReadOnlySpan<byte> row, int x, Span<byte> rgba)
		{
			var depth = _h.BitDepth;
			switch (_h.ColorType)
			{
				case 0:
					{
						var raw = Sample(row, x, depth);
						var g = depth == 16 ? (byte)(raw >> 8) : Scale(raw, depth);
						rgba[0] = rgba[1] = rgba[2] = g;
						rgba[3] = raw == _transparentGray ? (byte)0 : (byte)255;
						break;
					}
				case 2:
					{
						if (depth == 8)
						{
							rgba[0] = row[x * 3];
							rgba[1] = row[(x * 3) + 1];
							rgba[2] = row[(x * 3) + 2];
							rgba[3] = rgba[0] == _transparentR && rgba[1] == _transparentG && rgba[2] == _transparentB ? (byte)0 : (byte)255;
						}
						else
						{
							var r = BinaryPrimitives.ReadUInt16BigEndian(row[(x * 6)..]);
							var g = BinaryPrimitives.ReadUInt16BigEndian(row[((x * 6) + 2)..]);
							var b = BinaryPrimitives.ReadUInt16BigEndian(row[((x * 6) + 4)..]);
							rgba[0] = (byte)(r >> 8);
							rgba[1] = (byte)(g >> 8);
							rgba[2] = (byte)(b >> 8);
							rgba[3] = r == _transparentR && g == _transparentG && b == _transparentB ? (byte)0 : (byte)255;
						}

						break;
					}
				case 3:
					{
						var index = Sample(row, x, depth);
						if (index >= _paletteEntries)
							throw new FormatException("Invalid PNG: a pixel indexes past the end of the palette.");
						_palette.AsSpan(index * 4, 4).CopyTo(rgba);
						break;
					}
				case 4:
					{
						var size = depth / 8;
						rgba[0] = rgba[1] = rgba[2] = row[x * 2 * size];
						rgba[3] = row[(x * 2 * size) + size];
						break;
					}
				default:
					{
						var size = depth / 8;
						for (var c = 0; c < 4; c++)
							rgba[c] = row[((x * 4) + c) * size];
						break;
					}
			}
		}

		/// <summary>A sample of <paramref name="depth"/> bits, most significant first; 16 bits big-endian.</summary>
		private static int Sample(ReadOnlySpan<byte> row, int x, int depth) => depth switch
		{
			16 => BinaryPrimitives.ReadUInt16BigEndian(row[(x * 2)..]),
			8 => row[x],
			_ => (row[x * depth / 8] >> (8 - depth - (x * depth % 8))) & ((1 << depth) - 1),
		};

		/// <summary>Scales a 1-, 2-, 4- or 8-bit grey sample to 8 bits by replicating its bits.</summary>
		private static byte Scale(int value, int depth) => depth switch
		{
			1 => (byte)(value * 255),
			2 => (byte)(value * 85),
			4 => (byte)(value * 17),
			_ => (byte)value,
		};
	}

	/// <summary>CRC-32 as PNG defines it (ISO 3309, polynomial 0xEDB88320).</summary>
	internal static class Crc32
	{
		private static readonly uint[] Table = BuildTable();

		private static uint[] BuildTable()
		{
			var table = new uint[256];
			for (uint n = 0; n < 256; n++)
			{
				var c = n;
				for (var k = 0; k < 8; k++)
					c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
				table[n] = c;
			}

			return table;
		}

		public static uint Compute(ReadOnlySpan<byte> data)
		{
			var c = 0xFFFFFFFFu;
			foreach (var b in data)
				c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
			return c ^ 0xFFFFFFFFu;
		}
	}
}
