using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CznNoVpn;

public sealed class SmartProxyServer : IDisposable
{
    private readonly int _listenPort;
    private readonly SurfEasyClient _upstream;
    private readonly Action<string>? _logger;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    private long _tunneledCount;
    private long _directCount;

    public SmartProxyServer(int listenPort, SurfEasyClient upstream, Action<string>? logger = null)
    {
        _listenPort = listenPort;
        _upstream = upstream;
        _logger = logger;
    }

    public int Port => _listenPort;
    public long TunneledCount => Interlocked.Read(ref _tunneledCount);
    public long DirectCount => Interlocked.Read(ref _directCount);

    public void Start()
    {
        if (_listener != null)
            return;

        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Loopback, _listenPort);
        _listener.Start(128);
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        Log($"Smart split-proxy listening on 127.0.0.1:{_listenPort}");
    }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
            _listener?.Stop();
        }
        catch
        {
            // Ignore shutdown errors
        }
        finally
        {
            _listener = null;
        }
    }

    public static bool ShouldRouteViaTunnel(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;

        string h = host.Trim().ToLowerInvariant();
        int colon = h.IndexOf(':');
        if (colon > 0)
            h = h[..colon];

        // 1. NEVER tunnel game download CDNs, in-game patchers, or live game servers!
        // Keep them 100% DIRECT for maximum download speed and lowest in-game ping.
        if (h == "playstove.com" || h.EndsWith(".playstove.com", StringComparison.Ordinal) ||
            h.EndsWith(".game.onstove.com", StringComparison.Ordinal) ||
            h == "dl.onstove.com" || h.StartsWith("dl.", StringComparison.Ordinal) ||
            h.Contains("-dl.", StringComparison.Ordinal) ||
            h == "static-resource.onstove.com" ||
            h == "static-new.onstove.com" ||
            h == "msg-session.onstove.com")
        {
            return false;
        }

        // 2. Only tunnel STOVE region-check, store, and auth endpoints
        return h == "onstove.com" || h.EndsWith(".onstove.com", StringComparison.Ordinal) ||
               h == "onstove.vn" || h.EndsWith(".onstove.vn", StringComparison.Ordinal) ||
               h == "gate8.com" || h.EndsWith(".gate8.com", StringComparison.Ordinal);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener != null)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => HandleClientAsync(client, ct), CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                    Log($"Accept warning: {ex.Message}");
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken serverCt)
    {
        using (client)
        {
            client.NoDelay = true;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(serverCt);
            cts.CancelAfter(TimeSpan.FromMinutes(10));
            var ct = cts.Token;

            try
            {
                var clientStream = client.GetStream();
                var (requestLine, rawHeaders) = await ReadClientHeadersAsync(clientStream, ct);
                if (string.IsNullOrEmpty(requestLine))
                    return;

                string[] parts = requestLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                    return;

                string method = parts[0].ToUpperInvariant();
                string target = parts[1];

                // Serve PAC file if requested
                if (method == "GET" && (target.Equals("/proxy.pac", StringComparison.OrdinalIgnoreCase) ||
                                        target.EndsWith("/proxy.pac", StringComparison.OrdinalIgnoreCase)))
                {
                    await ServePacFileAsync(clientStream, ct);
                    return;
                }

                if (method == "CONNECT")
                {
                    await HandleConnectAsync(clientStream, target, ct);
                }
                else
                {
                    await HandlePlainHttpAsync(clientStream, method, target, parts.Length > 2 ? parts[2] : "HTTP/1.1", rawHeaders, ct);
                }
            }
            catch
            {
                // Normal socket close/reset
            }
        }
    }

    private async Task ServePacFileAsync(Stream clientStream, CancellationToken ct)
    {
        string pac = $$"""
            function FindProxyForURL(url, host) {
                host = host.toLowerCase();
                if (dnsDomainIs(host, "playstove.com") || shExpMatch(host, "*.playstove.com") ||
                    shExpMatch(host, "*.game.onstove.com") || host === "dl.onstove.com" ||
                    shExpMatch(host, "*-dl.*.onstove.com") || host === "static-resource.onstove.com") {
                    return "DIRECT";
                }
                if (dnsDomainIs(host, "onstove.com") || shExpMatch(host, "*.onstove.com") ||
                    dnsDomainIs(host, "onstove.vn")  || shExpMatch(host, "*.onstove.vn")  ||
                    dnsDomainIs(host, "gate8.com")   || shExpMatch(host, "*.gate8.com")) {
                    return "PROXY 127.0.0.1:{{_listenPort}}; DIRECT";
                }
                return "DIRECT";
            }
            """;

        byte[] body = Encoding.ASCII.GetBytes(pac);
        string header =
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: application/x-ns-proxy-autoconfig\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Connection: close\r\n\r\n";

        await clientStream.WriteAsync(Encoding.ASCII.GetBytes(header), ct);
        await clientStream.WriteAsync(body, ct);
        await clientStream.FlushAsync(ct);
    }

    private async Task HandleConnectAsync(NetworkStream clientStream, string target, CancellationToken ct)
    {
        var (host, port) = ParseHostPort(target, 443);
        bool useTunnel = ShouldRouteViaTunnel(host);

        Stream? remoteStream = null;
        TcpClient? directTcp = null;

        try
        {
            if (useTunnel)
            {
                Interlocked.Increment(ref _tunneledCount);
                Log($"[TUNNEL] {host}:{port}");
                using var dialCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                dialCts.CancelAfter(TimeSpan.FromSeconds(15));
                remoteStream = await _upstream.DialUpstreamAsync(host, port, dialCts.Token);
            }
            else
            {
                Interlocked.Increment(ref _directCount);
                directTcp = new TcpClient { NoDelay = true };
                using var dialCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                dialCts.CancelAfter(TimeSpan.FromSeconds(10));
                await directTcp.ConnectAsync(host, port, dialCts.Token);
                remoteStream = directTcp.GetStream();
            }

            byte[] okResp = "HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray();
            await clientStream.WriteAsync(okResp, ct);
            await clientStream.FlushAsync(ct);

            await PumpBidirectionalAsync(clientStream, remoteStream, ct);
        }
        finally
        {
            if (remoteStream != null)
                await remoteStream.DisposeAsync();
            directTcp?.Dispose();
        }
    }

    private async Task HandlePlainHttpAsync(
        NetworkStream clientStream,
        string method,
        string targetUrl,
        string httpVersion,
        List<string> headerLines,
        CancellationToken ct)
    {
        if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var uri))
            return;

        string host = uri.Host;
        int port = uri.Port > 0 ? uri.Port : 80;
        bool useTunnel = ShouldRouteViaTunnel(host);

        Stream? remoteStream = null;
        TcpClient? directTcp = null;

        try
        {
            if (useTunnel)
            {
                Interlocked.Increment(ref _tunneledCount);
                Log($"[TUNNEL-HTTP] {host}:{port}{uri.PathAndQuery}");
                remoteStream = await _upstream.DialUpstreamAsync(host, port, ct);
            }
            else
            {
                Interlocked.Increment(ref _directCount);
                directTcp = new TcpClient { NoDelay = true };
                await directTcp.ConnectAsync(host, port, ct);
                remoteStream = directTcp.GetStream();
            }

            var sb = new StringBuilder(512);
            sb.Append($"{method} {uri.PathAndQuery} {httpVersion}\r\n");
            foreach (string line in headerLines)
            {
                if (line.StartsWith("Proxy-Connection:", StringComparison.OrdinalIgnoreCase))
                    continue;
                sb.Append(line).Append("\r\n");
            }
            sb.Append("Connection: close\r\n\r\n");

            byte[] reqBytes = Encoding.ASCII.GetBytes(sb.ToString());
            await remoteStream.WriteAsync(reqBytes, ct);
            await remoteStream.FlushAsync(ct);

            await PumpBidirectionalAsync(clientStream, remoteStream, ct);
        }
        finally
        {
            if (remoteStream != null)
                await remoteStream.DisposeAsync();
            directTcp?.Dispose();
        }
    }

    private static async Task PumpBidirectionalAsync(Stream a, Stream b, CancellationToken ct)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = linkedCts.Token;

        Task t1 = CopyStreamPooledAsync(a, b, token);
        Task t2 = CopyStreamPooledAsync(b, a, token);

        await Task.WhenAny(t1, t2);
        linkedCts.Cancel();
    }

    private static async Task CopyStreamPooledAsync(Stream source, Stream destination, CancellationToken ct)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(32768);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int bytesRead = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                if (bytesRead <= 0)
                    break;
                await destination.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                await destination.FlushAsync(ct);
            }
        }
        catch
        {
            // Ignore stream termination
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<(string RequestLine, List<string> Headers)> ReadClientHeadersAsync(Stream stream, CancellationToken ct)
    {
        var sb = new StringBuilder(512);
        byte[] oneByte = new byte[1];
        int matchState = 0;

        while (sb.Length < 16384)
        {
            int n = await stream.ReadAsync(oneByte.AsMemory(0, 1), ct);
            if (n == 0)
                break;

            char c = (char)oneByte[0];
            sb.Append(c);

            if ((matchState == 0 || matchState == 2) && c == '\r')
                matchState++;
            else if ((matchState == 1 || matchState == 3) && c == '\n')
                matchState++;
            else if (c == '\r')
                matchState = 1;
            else
                matchState = 0;

            if (matchState == 4)
                break;
        }

        string all = sb.ToString();
        string[] lines = all.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
            return ("", new List<string>());

        return (lines[0], lines.Skip(1).ToList());
    }

    private static (string Host, int Port) ParseHostPort(string target, int defaultPort)
    {
        int lastColon = target.LastIndexOf(':');
        if (lastColon > 0 && int.TryParse(target[(lastColon + 1)..], out int port))
        {
            return (target[..lastColon], port);
        }
        return (target, defaultPort);
    }

    private void Log(string msg) => _logger?.Invoke(msg);

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}
