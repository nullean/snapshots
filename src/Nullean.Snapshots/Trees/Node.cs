namespace Nullean.Snapshots.Trees;

/// <summary>Where a node starts in its source text: 1-based line and column.</summary>
public readonly record struct SourceSpan(int Line, int Column)
{
	/// <inheritdoc />
	public override string ToString() => $"{Line}:{Column}";
}

/// <summary>A parsed JSON or YAML value, the common shape the tree differ compares.</summary>
/// <param name="Span">Where the node starts in its source, when the parser knows.</param>
public abstract record Node(SourceSpan? Span);

/// <summary>An object or mapping, entries in source order.</summary>
public sealed record MapNode(IReadOnlyList<KeyValuePair<ScalarNode, Node>> Entries, SourceSpan? Span = null) : Node(Span);

/// <summary>An array or sequence.</summary>
public sealed record SeqNode(IReadOnlyList<Node> Items, SourceSpan? Span = null) : Node(Span);

/// <summary>The kinds of scalar values.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "They name kinds of data, as JSON and YAML do.")]
public enum ScalarKind
{
	/// <summary><c>null</c>.</summary>
	Null,

	/// <summary><c>true</c> or <c>false</c>.</summary>
	Bool,

	/// <summary>An integer, kept as its source text so any size survives.</summary>
	Int,

	/// <summary>A floating point number, kept as its source text; <c>.inf</c>, <c>-.inf</c> and <c>.nan</c> included.</summary>
	Float,

	/// <summary>A string.</summary>
	String,
}

/// <summary>A scalar value.</summary>
/// <param name="Kind">Its kind.</param>
/// <param name="Canonical">
/// <c>null</c>, <c>true</c> or <c>false</c>; a number's text (see <see cref="NumberValue"/>); or the string itself.
/// </param>
/// <param name="Span">Where it starts in its source.</param>
public sealed record ScalarNode(ScalarKind Kind, string Canonical, SourceSpan? Span = null) : Node(Span)
{
	/// <summary>A string scalar.</summary>
	[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Mirrors ScalarKind.String.")]
	public static ScalarNode String(string value, SourceSpan? span = null) => new(ScalarKind.String, value, span);

	/// <summary>The null scalar.</summary>
	public static ScalarNode Null(SourceSpan? span = null) => new(ScalarKind.Null, "null", span);

	/// <summary>A boolean scalar.</summary>
	public static ScalarNode Bool(bool value, SourceSpan? span = null) => new(ScalarKind.Bool, value ? "true" : "false", span);

	/// <summary>True for <see cref="ScalarKind.Int"/> and <see cref="ScalarKind.Float"/>.</summary>
	public bool IsNumber => Kind is ScalarKind.Int or ScalarKind.Float;
}
