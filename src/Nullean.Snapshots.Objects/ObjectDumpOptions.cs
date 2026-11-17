using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Nullean.Snapshots.Objects;

/// <summary>How <see cref="ObjectSnapshot.Dump{T}(T, ObjectDumpOptions?)"/> reads an object.</summary>
public sealed class ObjectDumpOptions
{
	private readonly HashSet<string> _ignoredMembers = [with(StringComparer.Ordinal)];
	private readonly List<Type> _ignoredTypes = [];
	private readonly List<(Type Type, Func<object, string?> Render)> _renderers = [];
	private readonly HashSet<Type> _included = [];
	private readonly List<Type> _setTypes = [];

	/// <summary>Objects nested deeper than this are written as <c>&lt;max depth&gt;</c>. 16 by default.</summary>
	public int MaxDepth { get; set; } = 16;

	/// <summary>A collection longer than this lists this many items and then <c>&lt;+N more&gt;</c>. 1000 by default.</summary>
	public int MaxItems { get; set; } = 1000;

	/// <summary>
	/// Whether a type with no public members that was not named through <see cref="Include{T}"/> is an error. On by default under native AOT, where
	/// the trimmer removes the members of types the program never names, so such a type would silently dump as <c>{}</c>.
	/// </summary>
	internal bool RequireIncludedTypes { get; set; } = !RuntimeFeature.IsDynamicCodeSupported;

	internal bool IsIncluded(Type type) => _included.Contains(type);

	internal bool IsSetType(Type type)
	{
		for (var t = type; t is not null; t = t.BaseType)
		{
			var definition = t.IsGenericType ? t.GetGenericTypeDefinition() : t;
			if (definition == typeof(HashSet<>) || definition == typeof(System.Collections.Immutable.ImmutableHashSet<>) || _setTypes.Contains(definition))
				return true;
		}

		return false;
	}

	internal bool IsIgnoredMember(string name) => _ignoredMembers.Contains(name);

	internal bool IsIgnoredType(Type type) => _ignoredTypes.Exists(t => type.IsAssignableTo(t));

	/// <summary>Leaves out every property and field with this name, wherever it appears.</summary>
	public ObjectDumpOptions IgnoreMember(string name)
	{
		ArgumentException.ThrowIfNullOrEmpty(name);
		_ignoredMembers.Add(name);
		return this;
	}

	/// <summary>Leaves out every property and field whose declared type is, or derives from or implements, <typeparamref name="T"/>, such as a file system or a logger.</summary>
	public ObjectDumpOptions IgnoreType<T>()
	{
		_ignoredTypes.Add(typeof(T));
		return this;
	}

	/// <summary>
	/// Writes a value of type <typeparamref name="T"/> (or a type derived from it) as the string <paramref name="render"/> returns instead of
	/// walking its members. Returning null falls back to the default for that value. The first matching renderer wins.
	/// </summary>
	public ObjectDumpOptions Render<T>(Func<T, string?> render)
	{
		ArgumentNullException.ThrowIfNull(render);
		_renderers.Add((typeof(T), o => render((T)o)));
		return this;
	}

	/// <summary>
	/// Keeps the public properties and fields of <typeparamref name="T"/> when the program is trimmed or compiled with native AOT, which would otherwise
	/// remove the members of a type only reached through another object. Name every type in the graph: the root type is included for you.
	/// Without trimming it changes nothing.
	/// </summary>
	public ObjectDumpOptions Include<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicFields)] T>()
	{
		_included.Add(typeof(T));
		return this;
	}

	/// <summary>
	/// Sorts the items of a collection of this type (a type, or an open generic type such as <c>typeof(MySet&lt;&gt;)</c>, or a base class of it) by how they render, so
	/// an unordered collection dumps the same on every run. <see cref="HashSet{T}"/> and <c>ImmutableHashSet&lt;T&gt;</c> are always sorted. Interfaces are not
	/// consulted, as trimming removes them from a type's interface list and the dump would then differ between a normal and an AOT run.
	/// </summary>
	public ObjectDumpOptions SortAsSet(Type type)
	{
		ArgumentNullException.ThrowIfNull(type);
		_setTypes.Add(type.IsGenericType ? type.GetGenericTypeDefinition() : type);
		return this;
	}

	internal string? TryRender(object value)
	{
		foreach (var (type, render) in _renderers)
		{
			if (!type.IsInstanceOfType(value))
				continue;
			var text = render(value);
			if (text is not null)
				return text;
		}

		return null;
	}
}
