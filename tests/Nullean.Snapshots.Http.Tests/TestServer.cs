using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Nullean.Snapshots.Http.Tests;

/// <summary>An in-process HTTP server on a free localhost port, with the endpoints the HTTP snapshot tests need.</summary>
internal sealed class TestServer : IAsyncDisposable
{
	public const string Token = "tok-0123456789";

	private static readonly Lazy<TestServer> SharedServer = new(
		() =>
		{
			var server = new TestServer();
			AppDomain.CurrentDomain.ProcessExit += (_, _) => server.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
			return server;
		},
		LazyThreadSafetyMode.ExecutionAndPublication
	);

	/// <summary>One server for the whole test process, started on first use.</summary>
	public static TestServer Shared => SharedServer.Value;

	private readonly HttpListener _listener;
	private readonly CancellationTokenSource _stop = new();
	private readonly Task _loop;

	/// <summary>The preview of a site: <c>/api/catalog</c> has another value, and an extra property.</summary>
	private readonly bool _preview;

	public TestServer(bool preview = false)
	{
		_preview = preview;
		// FreePort releases the port again, so another test's server can take it before this one binds it: pick again.
		for (var attempt = 1; ; attempt++)
		{
			var port = FreePort();
			var listener = new HttpListener();
			listener.Prefixes.Add($"http://localhost:{port}/");
			try
			{
				listener.Start();
			}
			catch (HttpListenerException) when (attempt < 10)
			{
				listener.Close();
				continue;
			}

			_listener = listener;
			Port = port;
			break;
		}

		_loop = Task.Run(LoopAsync);
	}

	public int Port { get; }

	/// <summary><c>http://localhost:{port}</c>, without a trailing slash, as a <c>{{host}}</c> variable.</summary>
	public string Host => $"http://localhost:{Port}";

	public Uri BaseAddress => new($"http://localhost:{Port}/");

	/// <summary>
	/// A port that refuses connections. A port from <see cref="FreePort"/> is released at once, so a test that expects a
	/// refusal there can find another test's server listening on it. Port 1 is well-known: outside the range ports are
	/// handed out from, so no parallel test's server can take it, and a connection to it is refused on every platform.
	/// (A bound socket that never listens does not do: macOS drops the connection and the connect times out.)
	/// </summary>
	public const int RefusedPort = 1;

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
			catch (ObjectDisposedException)
			{
				return;
			}
			_ = Task.Run(() => Handle(context));
		}
	}

	private void Handle(HttpListenerContext context)
	{
		var request = context.Request;
		var response = context.Response;
		try
		{
			using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
			var body = reader.ReadToEnd();
			switch (request.Url!.AbsolutePath)
			{
				case "/api/login" when request.HttpMethod == "POST":
					Json(response, 200, $$"""{"token":"{{Token}}","user":{"name":"ada","roles":["admin","dev"]} }""");
					break;
				case "/api/orders":
					if (request.Headers["Authorization"] == "Bearer " + Token)
						Json(response, 200, /*lang=json,strict*/ """{"orders":[{"id":1,"total":12.5},{"id":2,"total":3}]}""");
					else
						Json(response, 401, /*lang=json,strict*/ """{"error":"unauthorized"}""");
					break;
				case "/api/catalog":
					// "self" holds this server's own address, as a page that links to itself does: only the origin scrub makes two sites equal.
					Json(
						response,
						200,
						_preview
							? $$"""{"items":[{"sku":"a","price":2.5,"badge":"new"},{"sku":"b","price":4}],"home":"http://preview.test/catalog","self":"{{Host}}/api/catalog"}"""
							: $$"""{"items":[{"sku":"a","price":1.5},{"sku":"b","price":3}],"home":"http://prod.test/catalog","self":"{{Host}}/api/catalog"}"""
					);
					break;
				case "/api/whoami":
					Json(response, 200, $$"""{"user":"{{request.QueryString["user"]}}"}""");
					break;
				case "/api/echo":
					// Echoes what it was sent, in its body and a header, as a careless server would: secrets included.
					var key = request.Headers["X-Api-Key"] ?? "";
					response.Headers["X-Echo"] = key;
					response.Headers["Set-Cookie"] = "session=" + key;
					Json(response, 200, $$"""{"key":"{{key}}","escaped":"{{Escape(key)}}","body":""" + System.Text.Json.JsonSerializer.Serialize(body) + "}");
					break;
				case "/api/text":
					Text(response, 200, "line one\n## not a section\n\nstatus: 500\n> quoted");
					break;
				case "/api/binary":
					response.StatusCode = 200;
					response.ContentType = "image/png";
					Write(response, [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0x00]);
					break;
				case "/api/gzip":
					Compressed(response, "gzip", s => new GZipStream(s, CompressionLevel.Fastest), /*lang=json,strict*/ """{"zipped":true}""");
					break;
				case "/api/plain":
					Json(response, 200, /*lang=json,strict*/ """{"zipped":true}""");
					break;
				case "/api/created":
					response.StatusCode = 201;
					response.Headers["Location"] = "/api/orders/3";
					response.Headers["X-Request-Id"] = "r-1";
					response.Headers["Authorization"] = "Bearer leaked";
					Json(response, 201, /*lang=json,strict*/ """{"id":3}""");
					break;
				case "/api/invalid-json":
					Json(response, 200, "{not json");
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

	// Every character as a \uXXXX escape: valid JSON that never contains the value as written.
	private static string Escape(string value) => string.Concat(value.Select(c => $"\\u{(int)c:X4}"));

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

	private static void Compressed(HttpListenerResponse response, string encoding, Func<Stream, Stream> compressor, string json)
	{
		using var buffer = new MemoryStream();
		using (var compress = compressor(buffer))
			compress.Write(Encoding.UTF8.GetBytes(json));
		response.StatusCode = 200;
		response.ContentType = "application/json; charset=utf-8";
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
		catch (ObjectDisposedException) { }
		_stop.Dispose();
	}
}
