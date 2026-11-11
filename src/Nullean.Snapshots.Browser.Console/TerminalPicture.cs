using Nullean.TerminalImages;
using Nullean.TerminalImages.Detection;

namespace Nullean.Snapshots.Browser.Console;

/// <summary>Draws a PNG in the terminal with the best protocol it has: Kitty, iTerm2, Sixel, or half blocks.</summary>
public static class TerminalPicture
{
	private static TerminalImageCapabilities? Capabilities;

	/// <summary>
	/// Draws <paramref name="png"/> to <paramref name="output"/>. False when the terminal cannot show images or drawing failed:
	/// a picture helps the reader and must never fail a run.
	/// </summary>
	public static bool TryDraw(TextWriter output, byte[] png)
	{
		ArgumentNullException.ThrowIfNull(output);
		ArgumentNullException.ThrowIfNull(png);
		try
		{
			Capabilities ??= Task.Run(async () => await TerminalImageDetector.DetectAsync().ConfigureAwait(false)).GetAwaiter().GetResult();
			var width = System.Console.IsOutputRedirected || System.Console.WindowWidth <= 20 ? 100 : Math.Min(System.Console.WindowWidth, 110);
			var renderer = new TerminalImageRenderer(Capabilities, new TerminalImageRenderOptions { MaxColumns = Math.Min(100, width - 2), MaxRows = 30 });
			return renderer.RenderPng(png, output);
		}
#pragma warning disable CA1031 // A picture is a convenience: whatever goes wrong drawing it, the text around it stands.
		catch (Exception)
#pragma warning restore CA1031
		{
			return false;
		}
	}
}
