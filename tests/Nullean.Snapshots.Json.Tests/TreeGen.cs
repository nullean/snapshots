using System.Globalization;
using System.Text;
using System.Text.Json;
using CsCheck;
using Nullean.Snapshots.Trees;

namespace Nullean.Snapshots.Json.Tests;

/// <summary>Generators for trees and the edits applied to them, and a writer that turns a tree into JSON.</summary>
public static class TreeGen
{
	/// <summary>Strings that trip up parsers and printers: empty, quotes, newlines, unicode, numeric- and keyword-looking.</summary>
	public static readonly Gen<string> NastyString = Gen.OneOf(
		// Whole fragments, not single chars: a char picked out of "😀" is a lone surrogate, which no encoder round-trips.
		Gen.OneOf("a", "b", " ", "\"", "\\", "\n", "\t", "{", "}", "é", "😀").Array[0, 6].Select(string.Concat),
		Gen.OneOf("", "1", "1.0", "-0", "true", "false", "null", "~", "yes", "no", "0x1F", "1_000", ".inf", "\u0000", "a\r\nb"));

	public static readonly Gen<ScalarNode> Scalar = Gen.OneOf(
		Gen.Const(ScalarNode.Null()),
		Gen.Bool.Select(b => ScalarNode.Bool(b)),
		Gen.Int[-1000, 1000].Select(i => new ScalarNode(ScalarKind.Int, i.ToString(CultureInfo.InvariantCulture))),
		Gen.OneOf("0", "-0", "9007199254740993", "-12345678901234567890").Select(t => new ScalarNode(ScalarKind.Int, t)),
		Gen.Double[-1e6, 1e6].Select(d => new ScalarNode(ScalarKind.Float, FloatText(d))),
		Gen.OneOf("1.5", "-0.0", "1e300", "2.5E-3").Select(t => new ScalarNode(ScalarKind.Float, t)),
		NastyString.Select(s => ScalarNode.String(s)));

	private static string FloatText(double d)
	{
		var text = d.ToString("R", CultureInfo.InvariantCulture);
		return text.AsSpan().IndexOfAny('.', 'E', 'e') >= 0 ? text : text + ".0";
	}

	/// <summary>Map keys: short, so maps share keys and the differ has overlap to work with.</summary>
	public static readonly Gen<string> Key = Gen.OneOf(Gen.String[Gen.Char["abcd"], 1, 2], Gen.OneOf("key with.space", "ü", "\"q\"", "id"));

	/// <summary>Trees up to five levels deep and six wide.</summary>
	public static readonly Gen<Node> Tree = Gen.Recursive<Node>((depth, inner) =>
	{
		if (depth >= 5)
			return Scalar.Select(s => (Node)s);
		var map = Gen.Select(Key, inner).Array[0, 6].Select(Map);
		var seq = inner.Array[0, 6].Select(items => (Node)new SeqNode(items));
		return Gen.Frequency((3, Scalar.Select(s => (Node)s)), (2, map), (2, seq));
	});

	public static Node Map((string Key, Node Value)[] entries)
	{
		var seen = new HashSet<string>(StringComparer.Ordinal);
		return new MapNode([.. entries.Where(e => seen.Add(e.Key)).Select(e => new KeyValuePair<ScalarNode, Node>(ScalarNode.String(e.Key), e.Value))]);
	}

	/// <summary>A map with at least <paramref name="minKeys"/> keys, each holding a tree.</summary>
	public static Gen<MapNode> MapWith(int minKeys) =>
		Gen.Select(Key, Tree).Array[minKeys, minKeys + 4]
			.Select(e => (MapNode)Map([.. e.Select((x, i) => ($"k{i}_{x.Item1}", x.Item2))]));

	/// <summary>Writes a tree as compact JSON, numbers as their text.</summary>
	public static string ToJson(Node node)
	{
		using var buffer = new MemoryStream();
		using (var writer = new Utf8JsonWriter(buffer))
			Write(writer, node);
		return Encoding.UTF8.GetString(buffer.ToArray());
	}

	private static void Write(Utf8JsonWriter writer, Node node)
	{
		switch (node)
		{
			case MapNode map:
				writer.WriteStartObject();
				foreach (var (key, value) in map.Entries)
				{
					writer.WritePropertyName(key.Canonical);
					Write(writer, value);
				}

				writer.WriteEndObject();
				break;
			case SeqNode seq:
				writer.WriteStartArray();
				foreach (var item in seq.Items)
					Write(writer, item);
				writer.WriteEndArray();
				break;
			case ScalarNode { Kind: ScalarKind.String } s:
				writer.WriteStringValue(s.Canonical);
				break;
			case ScalarNode { Kind: ScalarKind.Null }:
				writer.WriteNullValue();
				break;
			case ScalarNode s:
				writer.WriteRawValue(s.Canonical);
				break;
		}
	}

	/// <summary>Reverses the order of a map's entries, at every level.</summary>
	public static Node ReverseKeys(Node node) => node switch
	{
		MapNode m => new MapNode([.. m.Entries.Reverse().Select(e => new KeyValuePair<ScalarNode, Node>(e.Key, ReverseKeys(e.Value)))]),
		SeqNode q => new SeqNode([.. q.Items.Select(ReverseKeys)]),
		_ => node,
	};

	/// <summary>Counts nodes, for size budgets.</summary>
	public static int Count(Node node) => node switch
	{
		MapNode m => 1 + m.Entries.Sum(e => Count(e.Value)),
		SeqNode q => 1 + q.Items.Sum(Count),
		_ => 1,
	};
}
