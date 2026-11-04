namespace Nullean.HttpFileParser;

/// <summary>A problem in a <c>.http</c> file or an environment file, at a line.</summary>
public sealed class HttpFileError
{
	internal HttpFileError(string path, int line, string message)
	{
		Path = path;
		Line = line;
		Message = message;
	}

	/// <summary>The file the problem is in, as it was given to the parser.</summary>
	public string Path { get; }

	/// <summary>The 1-based line of the problem.</summary>
	public int Line { get; }

	/// <summary>What is wrong, in plain text.</summary>
	public string Message { get; }

	/// <summary>The problem as <c>path:line: message</c>.</summary>
	public override string ToString() => $"{Path}:{Line}: {Message}";
}

/// <summary>Thrown when a <c>.http</c> file or an environment file cannot be read. Carries every problem found, not just the first.</summary>
public sealed class HttpFileParseException : Exception
{
	/// <summary>Creates the exception from the problems found.</summary>
	public HttpFileParseException(IReadOnlyList<HttpFileError> errors) : base(Describe(errors)) => Errors = errors;

	/// <summary>Every problem found, in file order.</summary>
	public IReadOnlyList<HttpFileError> Errors { get; }

	private static string Describe(IReadOnlyList<HttpFileError> errors) =>
		errors.Count == 1 ? errors[0].ToString() : $"{errors.Count} errors:{Environment.NewLine}" + string.Join(Environment.NewLine, errors);
}
