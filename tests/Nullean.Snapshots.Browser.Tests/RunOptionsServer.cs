using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Nullean.Snapshots.Browser.Tests;

/// <summary>
/// A local page that posts to /save when its button is clicked, and records the user agent, the <c>X-Run</c> header and
/// the method of every request it receives.
/// </summary>
internal sealed class RunOptionsServer : IAsyncDisposable
{
	private const string Page = """
		<!doctype html><title>Run</title><link rel="icon" href="data:,">
		<button id="save" onclick="fetch('/save', { method: 'POST', body: 'x' })">Save</button>
		""";

	private readonly HttpListener _listener = new();
	private readonly Task _serving;
	private readonly List<string> _userAgents = [];
	private readonly List<string> _runHeaders = [];
	private readonly List<string> _methods = [];

	public RunOptionsServer()
	{
		Url = $"http://localhost:{FreePort()}/";
		_listener.Prefixes.Add(Url);
		_listener.Start();
		_serving = Task.Run(ServeAsync);
	}

	public string Url { get; }

	public IReadOnlyList<string> UserAgents => Snapshot(_userAgents);
	public IReadOnlyList<string> RunHeaders => Snapshot(_runHeaders);
	public IReadOnlyList<string> Methods => Snapshot(_methods);

	private static string[] Snapshot(List<string> list)
	{
		lock (list)
			return [.. list];
	}

	private async Task ServeAsync()
	{
		while (_listener.IsListening)
		{
			HttpListenerContext context;
			try
			{
				context = await _listener.GetContextAsync();
			}
			catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException or ArgumentException)
			{
				return;
			}

			Record(_userAgents, context.Request.UserAgent ?? "");
			Record(_runHeaders, context.Request.Headers["X-Run"] ?? "");
			Record(_methods, context.Request.HttpMethod + " " + context.Request.Url!.AbsolutePath);
			try
			{
				context.Response.ContentType = "text/html; charset=utf-8";
				await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(Page));
				context.Response.Close();
			}
			catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or IOException)
			{
			}
		}
	}

	private static void Record(List<string> list, string value)
	{
		lock (list)
			list.Add(value);
	}

	private static int FreePort()
	{
		using var probe = new TcpListener(IPAddress.Loopback, 0);
		probe.Start();
		return ((IPEndPoint)probe.LocalEndpoint).Port;
	}

	public async ValueTask DisposeAsync()
	{
		_listener.Stop();
		_listener.Close();
		await _serving;
	}
}
