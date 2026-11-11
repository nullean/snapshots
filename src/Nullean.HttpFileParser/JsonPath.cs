using System.Globalization;
using System.Text.Json;

namespace Nullean.HttpFileParser;

/// <summary>
/// The JSONPath subset response references support: <c>$</c>, <c>.name</c>, <c>["name"]</c>, <c>['name']</c> and <c>[n]</c>.
/// Expectations also accept <c>[*]</c> and <c>.*</c>, which select every element or property value.
/// </summary>
internal sealed class JsonPath
{
	private static readonly object Wildcard = new();

	private JsonPath(string text, IReadOnlyList<object> steps)
	{
		Text = text;
		Steps = steps;
	}

	public string Text { get; }

	/// <summary>Each step is a property name (<see cref="string"/>), an array index (<see cref="int"/>) or a wildcard.</summary>
	public IReadOnlyList<object> Steps { get; }

	/// <summary>Whether the path has a wildcard, so it can select several values.</summary>
	public bool HasWildcard => Steps.Contains(Wildcard);

	public static bool TryParse(string text, out JsonPath? path, out string? problem, bool allowWildcard = false)
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
				if (name == "*" && allowWildcard)
				{
					steps.Add(Wildcard);
					continue;
				}
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
				if (inside == "*" && allowWildcard)
				{
					steps.Add(Wildcard);
				}
				else if (inside.Length >= 2 && inside[0] is '"' or '\'' && inside[^1] == inside[0])
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
			if (ReferenceEquals(step, Wildcard))
				return false;
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

	/// <summary>Every element the path selects, in document order; empty when a step matches nothing.</summary>
	public IReadOnlyList<JsonElement> SelectAll(JsonElement root)
	{
		List<JsonElement> current = [root];
		foreach (var step in Steps)
		{
			List<JsonElement> next = [];
			foreach (var element in current)
				Step(element, step, next);
			current = next;
		}
		return current;
	}

	private static void Step(JsonElement element, object step, List<JsonElement> into)
	{
		if (ReferenceEquals(step, Wildcard))
		{
			if (element.ValueKind == JsonValueKind.Array)
				into.AddRange(element.EnumerateArray());
			else if (element.ValueKind == JsonValueKind.Object)
				into.AddRange(element.EnumerateObject().Select(p => p.Value));
		}
		else if (step is string name)
		{
			if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value))
				into.Add(value);
		}
		else if (element.ValueKind == JsonValueKind.Array && (int)step < element.GetArrayLength())
		{
			into.Add(element[(int)step]);
		}
	}

	/// <summary>A string's value without quotes; anything else as its JSON text.</summary>
	public static string ToText(JsonElement element) => element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : element.GetRawText();
}
