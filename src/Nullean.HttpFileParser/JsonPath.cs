using System.Globalization;
using System.Text.Json;

namespace Nullean.HttpFileParser;

/// <summary>The JSONPath subset response references support: <c>$</c>, <c>.name</c>, <c>["name"]</c>, <c>['name']</c> and <c>[n]</c>.</summary>
internal sealed class JsonPath
{
	private JsonPath(string text, IReadOnlyList<object> steps)
	{
		Text = text;
		Steps = steps;
	}

	public string Text { get; }

	/// <summary>Each step is a property name (<see cref="string"/>) or an array index (<see cref="int"/>).</summary>
	public IReadOnlyList<object> Steps { get; }

	public static bool TryParse(string text, out JsonPath? path, out string? problem)
	{
		path = null;
		problem = null;
		if (!text.StartsWith('$'))
		{
			problem = "a JSONPath starts with '$'";
			return false;
		}
		var steps = new List<object>();
		var i = 1;
		while (i < text.Length)
		{
			if (text[i] == '.')
			{
				var start = ++i;
				while (i < text.Length && text[i] is not ('.' or '['))
					i++;
				if (i == start)
				{
					problem = $"a property name is missing at position {start}";
					return false;
				}
				var name = text[start..i];
				if (name == "*" || name.StartsWith('.'))
				{
					problem = $"'{name}' is not supported; use '.name', '[\"name\"]' or '[n]'";
					return false;
				}
				steps.Add(name);
			}
			else if (text[i] == '[')
			{
				var end = text.IndexOf(']', i);
				if (end < 0)
				{
					problem = "unterminated '['";
					return false;
				}
				var inside = text[(i + 1)..end].Trim();
				if (inside.Length >= 2 && inside[0] is '"' or '\'' && inside[^1] == inside[0])
				{
					steps.Add(inside[1..^1]);
				}
				else if (int.TryParse(inside, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
				{
					steps.Add(index);
				}
				else
				{
					problem = $"'[{inside}]' is not supported; use '[n]' or '[\"name\"]'";
					return false;
				}
				i = end + 1;
			}
			else
			{
				problem = $"unexpected '{text[i]}' at position {i}";
				return false;
			}
		}
		path = new JsonPath(text, steps);
		return true;
	}

	/// <summary>Finds the element; <see langword="false"/> when a step matches nothing.</summary>
	public bool TrySelect(JsonElement root, out JsonElement found)
	{
		found = root;
		foreach (var step in Steps)
		{
			if (step is string name)
			{
				if (found.ValueKind != JsonValueKind.Object || !found.TryGetProperty(name, out found))
					return false;
			}
			else
			{
				var index = (int)step;
				if (found.ValueKind != JsonValueKind.Array || index >= found.GetArrayLength())
					return false;
				found = found[index];
			}
		}
		return true;
	}

	/// <summary>A string's value without quotes; anything else as its JSON text.</summary>
	public static string ToText(JsonElement element) =>
		element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : element.GetRawText();
}
