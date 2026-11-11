using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Nullean.Snapshots.Browser.Tests;

/// <summary>
/// A local HTTP server with a page that behaves like a site with a browser telemetry SDK: it loads a hashed chunk, swaps
/// its content on a link click without a new document (as htmx does), and posts JSON to a collector with a fetch
/// request after a short delay, as a batch processor with a scheduled delay does. The SDK is on only when the user agent ends in <c>telemetry-test</c>, as the real one is
/// off for the synthetics user agent, so a journey has to set one. A file:// page cannot post anywhere.
/// </summary>
internal sealed class TelemetryServer : IAsyncDisposable
{
	private const string Page =
		"""
		<!doctype html><title>Docs</title><link rel="icon" href="data:,">
		<nav><a id="start" href="/get-started">Elastic Fundamentals</a></nav>
		<main id="main-container"><h1>Home</h1></main>
		<script src="/assets/chunk-instrumentation-4f9a1c.js"></script>
		""";

	private const string Chunk =
		"""
		const on = navigator.userAgent.endsWith('telemetry-test');
		document.getElementById('start').addEventListener('click', (e) => {
		  e.preventDefault();
		  history.pushState(null, '', '/get-started');
		  document.getElementById('main-container').innerHTML = '<h1>Get started</h1>';
		});
		// The batch's scheduled delay. An export started while the page unloads (pagehide, visibilitychange) is never reported
		// by Chrome's DevTools protocol, to Playwright or to Nullean.Cdp, so a journey cannot assert on it.
		if (on) setTimeout(() => {
		  const record = { eventName: 'browser.navigation_timing', attributes: [{ key: 'session.id', value: { stringValue: 'abc123' } }] };
		  fetch('/_api/v1/o/l', {
		    method: 'POST',
		    body: JSON.stringify({ resourceLogs: [{
		      resource: { attributes: [{ key: 'service.name', value: { stringValue: 'docs-frontend' } }] },
		      scopeLogs: [{ logRecords: [record] }] }] }),
		  });
		}, 300);
		""";

	private readonly HttpListener _listener = new();
	private readonly Task _serving;

	public TelemetryServer()
	{
		// localhost, not 127.0.0.1: HttpListener on Windows needs no URL reservation for it.
		Url = $"http://localhost:{FreePort()}/";
		_listener.Prefixes.Add(Url);
		_listener.Start();
		_serving = Task.Run(ServeAsync);
	}

	public string Url { get; }

	private async Task ServeAsync()
	{
		while (_listener.IsListening)
		{
			HttpListenerContext context;
			try
			{
				context = await _listener.GetContextAsync();
			}
			catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
			{
				return;
			}

			_ = Task.Run(async () =>
			{
				var response = context.Response;
				try
				{
					var path = context.Request.Url!.AbsolutePath;
					if (path == "/_api/v1/o/l")
					{
						await context.Request.InputStream.CopyToAsync(Stream.Null);
						response.StatusCode = 200;
					}
					else if (path.StartsWith("/assets/chunk-instrumentation-", StringComparison.Ordinal))
					{
						response.ContentType = "text/javascript";
						await response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(Chunk));
					}
					else
					{
						// Every other path is the page: the link swaps the content and the URL, so a reload still finds it.
						response.ContentType = "text/html; charset=utf-8";
						await response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(Page));
					}

					response.Close();
				}
				catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or IOException) { }
			});
		}
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
