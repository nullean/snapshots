using AngleSharp.Dom;

namespace Nullean.Snapshots.Html;

/// <summary>
/// How HTML is normalised before it is stored or compared: which element to print, what to remove, and any custom
/// transform. Steps run in this order: select, remove elements, remove attributes, transforms, pretty-print.
/// </summary>
public sealed class HtmlSnapshotOptions
{
	private readonly List<string> _removeSelectors = [];
	private readonly List<(string? Selector, string[] Patterns)> _removeAttributes = [];
	private readonly List<Action<IElement>> _transforms = [];

	/// <summary>The CSS selector of the element whose content is printed; <see langword="null"/> prints the body.</summary>
	public string? Selector { get; private set; }

	internal IReadOnlyList<string> RemoveSelectors => _removeSelectors;
	internal IReadOnlyList<(string? Selector, string[] Patterns)> RemovedAttributes => _removeAttributes;
	internal IReadOnlyList<Action<IElement>> Transforms => _transforms;

	/// <summary>Prints the content of the first element matching <paramref name="selector"/> instead of the body.</summary>
	public HtmlSnapshotOptions Select(string selector)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(selector);
		Selector = selector;
		return this;
	}

	/// <summary>Removes every element matching <paramref name="selector"/>.</summary>
	public HtmlSnapshotOptions Remove(string selector)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(selector);
		_removeSelectors.Add(selector);
		return this;
	}

	/// <summary>
	/// Removes attributes from every element. A pattern is an attribute name, optionally with <c>*</c> wildcards:
	/// <c>RemoveAttributes("hx-*", "preload")</c>.
	/// </summary>
	public HtmlSnapshotOptions RemoveAttributes(params string[] patterns) => RemoveAttributesFrom(null, patterns);

	/// <summary>Removes attributes, as <see cref="RemoveAttributes"/> does, from elements matching <paramref name="selector"/>.</summary>
	public HtmlSnapshotOptions RemoveAttributesFrom(string? selector, params string[] patterns)
	{
		ArgumentNullException.ThrowIfNull(patterns);
		if (patterns.Length > 0)
			_removeAttributes.Add((selector, patterns));
		return this;
	}

	/// <summary>Runs <paramref name="transform"/> on the selected element after the removals, before printing.</summary>
	public HtmlSnapshotOptions Transform(Action<IElement> transform)
	{
		ArgumentNullException.ThrowIfNull(transform);
		_transforms.Add(transform);
		return this;
	}
}
