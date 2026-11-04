using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Nullean.HttpFileParser.Tests;

/// <summary>An in-process HTTP server on a free localhost port, with the endpoints the runner tests need.</summary>
internal sealed class TestServer : IAsyncDisposable
{
	public const string Token = "tok-0123456789";

	private readonly HttpListener _listener = new();
	private readonly CancellationTokenSource _stop = new();
	private readonly Task _loop;

	public TestServer()
	{
		Port = FreePort();
		_listener.Prefixes.Add($"http://localhost:{Port}/");
		_listener.Start();
		_loop = Task.Run(LoopAsync);
	}

	public int Port { get; }

	public Uri BaseAddress => new($"http://localhost:{Port}/");

	public static int FreePort()
	{
		var socket = new TcpListener(IPAddress.Loopback, 0);
		socket.Start();
		var port = ((IPEndPoint)socket.LocalEndpoint).Port;
		socket.Stop();
		return port;
	}

	private async Task LoopAsync()
	{
		while (!_stop.IsCancellationRequested)
		{
			HttpListenerContext context;
			try
			{
				context = await _listener.GetContextAsync();
			}
			catch (Exception) when (_stop.IsCancellationRequested)
			{
				return;
			}
			catch (HttpListenerException)
			{
				return;
			}
			_ = Task.Run(() => Handle(context));
		}
	}

	private static void Handle(HttpListenerContext context)
	{
		var request = context.Request;
		var response = context.Response;
		try
		{
			using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
			var body = reader.ReadToEnd();
			var path = request.Url!.AbsolutePath;
			switch (path)
			{
				case "/login" when request.HttpMethod == "POST":
					Json(response, 200, $$"""{"token":"{{Token}}","user":{"name":"ada","roles":["admin","dev"]},"echo":{{(body.Length == 0 ? "null" : body)}}}""");
					break;
				case "/secure":
					if (request.Headers["Authorization"] == "Bearer " + Token)
						Json(response, 200, /*lang=json,strict*/ """{"ok":true}""");
					else
						Json(response, 401, /*lang=json,strict*/ """{"ok":false}""");
					break;
				case "/redirect":
					response.StatusCode = 302;
					response.RedirectLocation = "/target";
					break;
				case "/redirect-303":
					response.StatusCode = 303;
					response.RedirectLocation = "/echo";
					break;
				case "/loop":
					response.StatusCode = 302;
					response.RedirectLocation = "/loop";
					break;
				case "/target":
					Text(response, 200, "target reached");
					break;
				case "/gzip":
					Compressed(response, "gzip", s => new GZipStream(s, CompressionLevel.Fastest), "zipped hello");
					break;
				case "/brotli":
					Compressed(response, "br", s => new BrotliStream(s, CompressionLevel.Fastest), "brotli hello");
					break;
				case "/echo":
					var echo = request.Headers["X-Echo"] ?? "";
					response.Headers["X-Echo-Back"] = echo;
					Json(response, 200, $$"""{"method":"{{request.HttpMethod}}","header":"{{echo}}","q":"{{request.QueryString["q"]}}","cookie":"{{request.Headers["Cookie"]}}","contentType":"{{request.ContentType}}","body":{{System.Text.Json.JsonSerializer.Serialize(body)}}}""");
					break;
				case "/slow":
					Thread.Sleep(2000);
					Text(response, 200, "slow");
					break;
				case "/set-cookie":
					response.Headers.Add("Set-Cookie", "session=abc123; Path=/");
					Text(response, 200, "set");
					break;
				default:
					Text(response, 404, "not found");
					break;
			}
		}
		finally
		{
			response.Close();
		}
	}

	private static void Json(HttpListenerResponse response, int status, string json)
	{
		response.StatusCode = status;
		response.ContentType = "application/json; charset=utf-8";
		Write(response, Encoding.UTF8.GetBytes(json));
	}

	private static void Text(HttpListenerResponse response, int status, string text)
	{
		response.StatusCode = status;
		response.ContentType = "text/plain; charset=utf-8";
		Write(response, Encoding.UTF8.GetBytes(text));
	}

	private static void Compressed(HttpListenerResponse response, string encoding, Func<Stream, Stream> compressor, string text)
	{
		using var buffer = new MemoryStream();
		using (var compress = compressor(buffer))
			compress.Write(Encoding.UTF8.GetBytes(text));
		response.StatusCode = 200;
		response.ContentType = "text/plain; charset=utf-8";
		response.AddHeader("Content-Encoding", encoding);
		Write(response, buffer.ToArray());
	}

	private static void Write(HttpListenerResponse response, byte[] bytes)
	{
		response.ContentLength64 = bytes.Length;
		response.OutputStream.Write(bytes);
	}

	public async ValueTask DisposeAsync()
	{
		_stop.Cancel();
		_listener.Stop();
		_listener.Close();
		try
		{
			await _loop;
		}
		catch (ObjectDisposedException)
		{
		}
		_stop.Dispose();
	}
}
