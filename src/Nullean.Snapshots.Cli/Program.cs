using Nullean.Argh;
using Nullean.Snapshots.Cli;

var app = new ArghApp();
app.UseCliDescription("snap: review and accept Nullean.Snapshots snapshots");
app.Map<SnapCommands>();

return await app.RunAsync(args);
