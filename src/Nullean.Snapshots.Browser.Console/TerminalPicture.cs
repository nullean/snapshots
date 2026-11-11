using Nullean.TerminalImages;
using Nullean.TerminalImages.Detection;

namespace Nullean.Snapshots.Browser.Console;

/// <summary>Draws a PNG in the terminal with the best protocol it has: Kitty, iTerm2, Sixel, or half blocks.</summary>
public static class TerminalPicture
{
	private static TerminalImageCapabilities? Capabilities;

	/// <summary>
	/// Draws <paramref name="png"/> to <paramref name="output"/> at the size the terminal gives it, within 100 columns and 30 rows.
	/// False when the terminal cannot show images or drawing failed: a picture helps the reader and must never fail a run.
	/// </summary>
	public static bool TryDraw(TextWriter output, byte[] png) => TryDraw(output, png, enlarge: false, maxColumns: 100, maxRows: 30);

	/// <summary>
	/// Draws <paramref name="png"/> within <paramref name="maxColumns"/> by <paramref name="maxRows"/> cells. With
	/// <paramref name="enlarge"/> a small picture is made bigger to fill them, smoothly, so a crop of a few dozen pixels is readable
	/// on a screen whose pixels are small; the picture itself is not changed. False when the terminal cannot show images or drawing failed.
	/// </summary>
	public static bool TryDraw(TextWriter output, byte[] png, bool enlarge, int maxColumns, int maxRows)
	{
		ArgumentNullException.ThrowIfNull(output);
		ArgumentNullException.ThrowIfNull(png);
		try
		{
			Capabilities ??= Task.Run(async () => await TerminalImageDetector.DetectAsync().ConfigureAwait(false)).GetAwaiter().GetResult();
			var width = System.Console.IsOutputRedirected || System.Console.WindowWidth <= 20 ? 100 : Math.Min(System.Console.WindowWidth, 110);
			var renderer = new TerminalImageRenderer(
				Capabilities,
				new TerminalImageRenderOptions { MaxColumns = Math.Min(maxColumns, width - 2), MaxRows = maxRows, AllowUpscale = enlarge }
			);
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
