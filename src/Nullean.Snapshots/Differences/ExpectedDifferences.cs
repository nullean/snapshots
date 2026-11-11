using System.IO.Abstractions;
using System.Text;
using Nullean.Snapshots.Trees;

namespace Nullean.Snapshots.Differences;

/// <summary>A difference a rule accounts for.</summary>
/// <param name="Difference">The difference.</param>
/// <param name="Rule">The first rule that selects it.</param>
public sealed record ExplainedDifference(TreeDifference Difference, DifferenceRule Rule);

/// <summary>What the rules made of the differences between a baseline and a target.</summary>
/// <param name="Unexplained">The differences no rule accounts for: these fail the run.</param>
/// <param name="UnexplainedFacts">Where each of them is, for suggesting or recording rules.</param>
/// <param name="Explained">The differences that rules account for.</param>
/// <param name="Stale">
/// Rules that selected nothing, though they should have: not <c>maybe</c>, and from the sidecar or an <c>include?</c>.
/// A rule from an <c>include</c> is shared and says nothing about one file not needing it.
/// </param>
public sealed record RuleApplication(
	IReadOnlyList<TreeDifference> Unexplained,
	IReadOnlyList<DifferenceFacts> UnexplainedFacts,
	IReadOnlyList<ExplainedDifference> Explained,
	IReadOnlyList<DifferenceRule> Stale
)
{
	/// <summary>Whether the run passes: everything is explained and no rule is stale.</summary>
	public bool IsClean => Unexplained.Count == 0 && Stale.Count == 0;
}

/// <summary>Rules whose targets a format has read.</summary>
public sealed class BoundRules
{
	internal BoundRules(RuleSet set, IReadOnlyList<(DifferenceRule Rule, IRuleTarget? Target)> rules)
	{
		Set = set;
		Rules = rules;
	}

	/// <summary>The rules and their files.</summary>
	public RuleSet Set { get; }

	internal IReadOnlyList<(DifferenceRule Rule, IRuleTarget? Target)> Rules { get; }
}

/// <summary>
/// Expected differences between a baseline and a target, written as rules in a checked-in <c>.differences</c> file: what
/// may differ between two sites, so the run fails only on what nobody expected.
/// </summary>
public static class ExpectedDifferences
{
	/// <summary>Reads each rule's target with <paramref name="targets"/>.</summary>
	/// <exception cref="ExpectedDifferencesException">A target is not one this format knows.</exception>
	public static BoundRules Bind(RuleSet rules, RuleTargets targets)
	{
		ArgumentNullException.ThrowIfNull(rules);
		ArgumentNullException.ThrowIfNull(targets);
		var bound = new List<(DifferenceRule, IRuleTarget?)>();
		var errors = new List<string>();
		foreach (var rule in rules.Rules)
		{
			if (rule.Target.Count == 0)
			{
				bound.Add((rule, null));
				continue;
			}

			try
			{
				bound.Add((rule, targets.ParseTarget(rule.Verb, rule.Target)));
			}
			catch (FormatException e)
			{
				errors.Add($"{rule.Where}: {e.Message}");
			}
		}

		if (errors.Count > 0)
			throw new ExpectedDifferencesException(errors);
		return new BoundRules(rules, bound);
	}

	/// <summary>Sorts <paramref name="differences"/> into the ones a rule accounts for and the ones none does.</summary>
	public static RuleApplication Apply(IReadOnlyList<TreeDifference> differences, Node baseline, Node actual, BoundRules rules, RuleTargets targets)
	{
		ArgumentNullException.ThrowIfNull(differences);
		ArgumentNullException.ThrowIfNull(baseline);
		ArgumentNullException.ThrowIfNull(actual);
		ArgumentNullException.ThrowIfNull(rules);
		ArgumentNullException.ThrowIfNull(targets);
		var used = new HashSet<DifferenceRule>();
		var unexplained = new List<TreeDifference>();
		var unexplainedFacts = new List<DifferenceFacts>();
		var explained = new List<ExplainedDifference>();
		foreach (var difference in differences)
		{
			var facts = DifferenceFacts.Create(difference, baseline, actual, targets.HeadingKey);
			DifferenceRule? first = null;
			foreach (var (rule, target) in rules.Rules)
			{
				if (!Explains(rule, target, facts))
					continue;
				used.Add(rule);
				first ??= rule;
			}

			if (first is null)
			{
				unexplained.Add(difference);
				unexplainedFacts.Add(facts);
			}
			else
				explained.Add(new ExplainedDifference(difference, first));
		}

		var stale = rules.Rules.Select(r => r.Rule).Where(r => !r.Maybe && r.Origin != RuleOrigin.Shared && !used.Contains(r)).ToList();
		return new RuleApplication(unexplained, unexplainedFacts, explained, stale);
	}

	private static bool Explains(DifferenceRule rule, IRuleTarget? target, DifferenceFacts facts)
	{
		if (facts.SectionIndex < 0)
			return false;
		if (rule.Section is not null && (facts.Heading is null || !Glob.IsMatch(rule.Section, facts.Heading)))
			return false;
		var fits = rule.Verb switch
		{
			RuleVerb.Added => facts.Kind == DifferenceKind.Unexpected,
			RuleVerb.Removed => facts.Kind == DifferenceKind.Missing,
			RuleVerb.Changed => facts.Kind is DifferenceKind.Changed or DifferenceKind.TypeChanged,
			_ => true,
		};
		if (!fits)
			return false;
		if (rule.Old is not null && !rule.Old.Matches(facts.OldText))
			return false;
		if (rule.New is not null && !rule.New.Matches(facts.NewText))
			return false;
		if (target is null)
		{
			// No target: a rule about the whole section, or (changed, ignore) about anything in it.
			return rule.Verb switch
			{
				RuleVerb.Added or RuleVerb.Removed => facts.Path.Count == 0,
				RuleVerb.Changed => facts.Path.Count > 0,
				_ => true,
			};
		}

		return facts.Path.Count > 0 && target.Matches(facts, rule.Verb);
	}

	/// <summary>
	/// The rule lines (without their section) that would account for a difference: <c>added link "What's new"</c>,
	/// <c>changed status 200 -> 404</c>. Pasting them under the section's heading is how a person or an agent says the
	/// difference is expected.
	/// </summary>
	public static IReadOnlyList<string> Suggest(DifferenceFacts facts, RuleTargets targets)
	{
		ArgumentNullException.ThrowIfNull(facts);
		ArgumentNullException.ThrowIfNull(targets);
		var verb = facts.Kind switch
		{
			DifferenceKind.Unexpected => "added",
			DifferenceKind.Missing => "removed",
			DifferenceKind.Changed or DifferenceKind.TypeChanged => "changed",
			_ => "ignore",
		};
		IReadOnlyList<string> texts = facts.Path.Count == 0 ? [string.Empty] : targets.Suggest(facts);
		var values = string.Empty;
		if (verb == "changed" && facts.OldText is { } old && facts.NewText is { } @new)
		{
			values = IsShort(old) && IsShort(@new) ? $" {RuleFileParser.Quote(old)} -> {RuleFileParser.Quote(@new)}" : $"  # {Shorten(old)} -> {Shorten(@new)}";
		}

		return [.. texts.Select(t => verb + (t.Length > 0 ? " " + t : string.Empty) + values)];
	}

	private static bool IsShort(string text) => text.Length <= 80 && !text.Contains('\n', StringComparison.Ordinal);

	private static string Shorten(string text)
	{
		var line = text.Split('\n')[0].Trim();
		return line.Length > 40 ? line[..40] + "..." : line;
	}

	/// <summary>
	/// Appends a rule for every difference to <paramref name="file"/>, under a heading for its section and a comment
	/// saying <paramref name="note"/>. The file must be an <c>include?</c> of the rules, so what is recorded is read
	/// the next time, and is the only file this ever writes.
	/// </summary>
	/// <returns>The rule lines written.</returns>
	/// <exception cref="InvalidOperationException">The file is not an <c>include?</c> of these rules.</exception>
	public static IReadOnlyList<string> Record(RuleSet rules, string file, IReadOnlyList<DifferenceFacts> unexplained, RuleTargets targets, string note)
	{
		ArgumentNullException.ThrowIfNull(rules);
		ArgumentNullException.ThrowIfNull(unexplained);
		var fs = rules.Scoped;
		var full = fs.Path.GetFullPath(file);
		if (!rules.Files.Any(f => f.Origin == RuleOrigin.Optional && RuleLoader.Comparer.Equals(f.Path, full)))
		{
			throw new InvalidOperationException(
				$"{file} is not an 'include?' of the rules, so nothing would read what is recorded there. Add 'include? <path>' to the rules file next to the journey or .http file first."
			);
		}

		var text = new StringBuilder();
		var written = new List<string>();
		foreach (var section in unexplained.Where(f => f.Heading is not null).GroupBy(f => f.Heading!.ToString(), StringComparer.Ordinal))
		{
			var lines = section.SelectMany(f => Suggest(f, targets)).Distinct(StringComparer.Ordinal).ToList();
			text.Append("## ").Append(section.Key).Append('\n');
			foreach (var line in lines)
				text.Append(line).Append('\n');
			written.AddRange(lines);
		}

		if (written.Count == 0)
			return written;
		var existing = fs.File.Exists(full) ? fs.File.ReadAllText(full) : string.Empty;
		var content = new StringBuilder(existing);
		if (existing.Length > 0 && !existing.EndsWith('\n'))
			content.Append('\n');
		if (existing.Length > 0)
			content.Append('\n');
		content.Append("# ").Append(note.Replace('\n', ' ')).Append('\n').Append(text);
		Write(fs, full, content.ToString());
		return written;
	}

	/// <summary>Removes the stale rules of <paramref name="file"/>, and the sections they leave empty. Returns how many rules went.</summary>
	public static int Prune(RuleSet rules, string file, RuleApplication application)
	{
		ArgumentNullException.ThrowIfNull(rules);
		ArgumentNullException.ThrowIfNull(application);
		var fs = rules.Scoped;
		var full = fs.Path.GetFullPath(file);
		var stale = application.Stale.Where(r => RuleLoader.Comparer.Equals(r.Where.File, full)).Select(r => r.Where.Line).ToHashSet();
		if (stale.Count == 0 || !fs.File.Exists(full))
			return 0;
		var lines = fs.File.ReadAllText(full).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
		var kept = new List<string>();
		var header = -1;
		var rulesInSection = 0;
		for (var i = 0; i < lines.Length; i++)
		{
			var line = lines[i];
			var trimmed = line.Trim();
			if (stale.Contains(i + 1))
				continue;
			if (trimmed.StartsWith("##", StringComparison.Ordinal))
			{
				DropIfEmpty();
				header = kept.Count;
				rulesInSection = 0;
			}
			else if (trimmed.Length > 0 && !trimmed.StartsWith('#'))
				rulesInSection++;
			kept.Add(line);
		}

		DropIfEmpty();

		void DropIfEmpty()
		{
			if (header >= 0 && rulesInSection == 0)
				kept.RemoveRange(header, kept.Count - header);
		}

		Write(fs, full, string.Join('\n', kept));
		return stale.Count;
	}

	private static void Write(IFileSystem fs, string path, string content)
	{
		var directory = fs.Path.GetDirectoryName(path);
		if (!string.IsNullOrEmpty(directory))
			fs.Directory.CreateDirectory(directory);
		var temporary = path + ".tmp";
		fs.File.WriteAllText(temporary, content.Replace("\r\n", "\n", StringComparison.Ordinal));
		fs.File.Move(temporary, path, overwrite: true);
	}
}
