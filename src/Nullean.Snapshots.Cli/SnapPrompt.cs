namespace Nullean.Snapshots.Cli;

/// <summary>Asks the person reviewing snapshots what to do next.</summary>
internal interface ISnapPrompt
{
	/// <summary>Shows <paramref name="question"/> and returns the first letter answered, or null when input has ended.</summary>
	char? Ask(string question);
}

/// <summary>
/// Reads single key presses on a terminal; with redirected input, one answer per line, so a review can be scripted
/// (<c>printf 'a\nr\n' | snap review</c>).
/// </summary>
internal sealed class ConsolePrompt : ISnapPrompt
{
	public char? Ask(string question)
	{
		Console.Write(question);
		if (Console.IsInputRedirected)
		{
			var line = Console.ReadLine();
			Console.WriteLine();
			return line is null ? null : line.Trim() is [var first, ..] ? char.ToLowerInvariant(first) : ' ';
		}

		var key = Console.ReadKey(intercept: true);
		Console.WriteLine(key.KeyChar);
		return char.ToLowerInvariant(key.KeyChar);
	}

	/// <summary>Colour only on a terminal, and never when <c>NO_COLOR</c> is set or the terminal is dumb.</summary>
	public static bool UseColor =>
		!Console.IsOutputRedirected && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR")) && Environment.GetEnvironmentVariable("TERM") !=
			"dumb";
}
