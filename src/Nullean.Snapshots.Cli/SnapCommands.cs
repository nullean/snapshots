using System.IO.Abstractions;
using Nullean.Argh;

namespace Nullean.Snapshots.Cli;

/// <summary>
/// Review snapshots from Nullean.Snapshots. Exit codes: 0 ok, 1 pending snapshots, 2 usage, 3 IO error.
/// </summary>
internal sealed class SnapCommands
{
	/// <summary>List pending snapshots. Exits 1 when there are any.</summary>
	/// <param name="json">Print a JSON array of {key, ext, state, verified, received} instead.</param>
	/// <param name="root">The folder to search from. Defaults to the current folder.</param>
	public static int Status(bool json = false, string? root = null) => Cli(root).Status(json);

	/// <summary>Show how a received snapshot differs from its verified file.</summary>
	/// <param name="key">A key from 'snap status', any unique suffix of one, or a glob.</param>
	/// <param name="root">The folder to search from. Defaults to the current folder.</param>
	public static int Diff([Argument] string key, string? root = null) => Cli(root).Diff(key);

	/// <summary>Accept received snapshots: write them as verified and delete the received files.</summary>
	/// <param name="keys">Keys from 'snap status', unique suffixes of them, or globs.</param>
	/// <param name="all">Accept every pending snapshot.</param>
	/// <param name="root">The folder to search from. Defaults to the current folder.</param>
	public static int Accept([Argument] string[] keys, bool all = false, string? root = null) => Cli(root).Accept(keys, all);

	/// <summary>Reject received snapshots: delete the received files and keep the verified ones.</summary>
	/// <param name="keys">Keys from 'snap status', unique suffixes of them, or globs.</param>
	/// <param name="all">Reject every pending snapshot.</param>
	/// <param name="root">The folder to search from. Defaults to the current folder.</param>
	public static int Reject([Argument] string[] keys, bool all = false, string? root = null) => Cli(root).Reject(keys, all);

	/// <summary>Review pending snapshots one by one: see each diff, then accept, reject or skip it.</summary>
	/// <param name="root">The folder to search from. Defaults to the current folder.</param>
	public static int Review(string? root = null) => Cli(root).Review(new ConsolePrompt(), ConsolePrompt.UseColor);

	/// <summary>List verified snapshots the latest test run did not use. Exits 1 when there are any.</summary>
	/// <param name="json">Print a JSON array of {project, verified} instead.</param>
	/// <param name="delete">Delete them. Run the whole test suite first: a filtered run skips snapshots.</param>
	/// <param name="root">The folder to search from. Defaults to the current folder.</param>
	public static int Orphans(bool json = false, bool delete = false, string? root = null) => Cli(root).Orphans(json, delete);

	private static SnapCli Cli(string? root) =>
		new(new FileSystem(), root ?? Directory.GetCurrentDirectory(), Console.Out, Console.Error);
}
