using System.Globalization;

namespace Nullean.Snapshots.Trees;

/// <summary>
/// A number reduced to sign, significant digits and exponent, so <c>1</c>, <c>1.0</c> and <c>10e-1</c> compare equal
/// without going through <see cref="double"/> (which would lose big integers). Also holds infinities and NaN.
/// </summary>
public readonly record struct NumberValue
{
	private enum Special : byte { Finite, PositiveInfinity, NegativeInfinity, NaN }

	private readonly Special _special;

	private NumberValue(Special special, bool negative, string digits, long exponent)
	{
		_special = special;
		Negative = negative;
		Digits = digits;
		Exponent = exponent;
	}

	/// <summary>True for NaN (<c>.nan</c>); NaN equals NaN here, as a snapshot value.</summary>
	public bool IsNaN => _special == Special.NaN;

	/// <summary>True for positive or negative infinity.</summary>
	public bool IsInfinity => _special is Special.PositiveInfinity or Special.NegativeInfinity;

	/// <summary>Whether the number is below zero; zero is never negative, so <c>-0</c> equals <c>0</c>.</summary>
	public bool Negative { get; }

	/// <summary>The significant digits without leading or trailing zeros; empty for zero and the specials.</summary>
	public string Digits { get; }

	/// <summary>The power of ten the digits are multiplied by.</summary>
	public long Exponent { get; }

	/// <summary>
	/// Parses JSON number syntax (<c>-12.5e+3</c>), YAML 1.2 floats' <c>.inf</c>, <c>-.inf</c>, <c>.nan</c> and their
	/// <c>inf</c>/<c>nan</c> spellings. Returns false for anything else.
	/// </summary>
	public static bool TryParse(string text, out NumberValue value)
	{
		value = default;
		var s = text.AsSpan().Trim();
		var negative = false;
		if (s.Length > 0 && (s[0] == '-' || s[0] == '+'))
		{
			negative = s[0] == '-';
			s = s[1..];
		}

		var lower = s.ToString().ToLowerInvariant();
		if (lower is ".inf" or "inf" or "infinity")
		{
			value = new(negative ? Special.NegativeInfinity : Special.PositiveInfinity, negative, "", 0);
			return true;
		}

		if (lower is ".nan" or "nan")
		{
			value = new(Special.NaN, false, "", 0);
			return true;
		}

		var i = 0;
		var digits = new System.Text.StringBuilder();
		var any = false;
		while (i < s.Length && char.IsAsciiDigit(s[i]))
		{
			digits.Append(s[i++]);
			any = true;
		}

		long exponent = 0;
		if (i < s.Length && s[i] == '.')
		{
			i++;
			while (i < s.Length && char.IsAsciiDigit(s[i]))
			{
				digits.Append(s[i++]);
				exponent--;
				any = true;
			}
		}

		if (!any)
			return false;

		if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
		{
			i++;
			var start = i;
			if (i < s.Length && (s[i] == '-' || s[i] == '+'))
				i++;
			if (i == s.Length || !char.IsAsciiDigit(s[i]))
				return false;
			while (i < s.Length && char.IsAsciiDigit(s[i]))
				i++;
			if (!long.TryParse(s[start..i], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var e))
				return false;
			exponent += e;
		}

		if (i != s.Length)
			return false;

		var d = digits.ToString().TrimStart('0');
		var trimmed = d.TrimEnd('0');
		exponent += d.Length - trimmed.Length;
		value = trimmed.Length == 0
			? new(Special.Finite, false, "", 0)
			: new(Special.Finite, negative, trimmed, exponent);
		return true;
	}
}
