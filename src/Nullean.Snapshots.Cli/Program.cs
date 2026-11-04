using Nullean.Argh;
using Nullean.Snapshots.Cli;

var app = new ArghApp();
app.MapRoot(RootCommand.Review);
app.Map<SnapCommands>();
app.MapNamespace<JourneyCommands>("journey");

return await app.RunAsync(args);

namespace Nullean.Snapshots.Cli
{
	internal static class RootCommand
	{
		/// <summary>
		/// snap: review and accept Nullean.Snapshots snapshots. Without a command, walks through the pending snapshots one
		/// by one.
		/// </summary>
		/// <param name="root">The folder to search from. Defaults to the current folder.</param>
		public static int Review(string? root = null) => SnapCommands.Review(root);
	}
}
