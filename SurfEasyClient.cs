using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CznNoVpn;

public sealed class ProxyEndpoint
{
    [JsonPropertyName("ip")]
    public string Ip { get; set; } = "";

    [JsonPropertyName("port")]
    public int Port { get; set; } = 443;

    [JsonPropertyName("host")]
    public string Host { get; set; } = "";

    [JsonPropertyName("country")]
    public string Country { get; set; } = "";

    [JsonPropertyName("latency_ms")]
    public long LatencyMs { get; set; } = 9999;

    public override string ToString() => $"{Ip}:{Port} ({Country}, {LatencyMs}ms)";
}

public sealed class CachedProxyState
{
    public string SubscriberEmail { get; set; } = "";
    public string SubscriberPassword { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string AssignedDeviceId { get; set; } = "";
    public string AssignedDeviceIdHash { get; set; } = "";
    public string DevicePassword { get; set; } = "";
    public DateTime UpdatedAtUtc { get; set; }
    public List<ProxyEndpoint> Endpoints { get; set; } = new();
}

public sealed class SurfEasyClient : IDisposable
{
    private const string ApiBaseUrl = "https://api2.sec-tunnel.com/v4";
    private const string ApiLogin = "se0316";
    private const string ApiSecret = "SILrMEPBmJuhomxWkfm3JalqHX2Eheg1YhlEZiMh8II";
    private const string ClientType = "se0316";
    private const string ClientVersion = "Stable 114.0.5282.21";
    private const string DeviceName = "Opera-Browser-Client";
    private const string OperatingSystemHeader = "Windows";
    private const string UserAgentHeader = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36 OPR/114.0.0.0";

    private readonly HttpClient _httpClient;
    private readonly CookieContainer _cookies = new();
    private readonly string _cacheFilePath;
    private readonly Action<string>? _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    private CachedProxyState _state = new();
    private int _currentEndpointIndex;
    private int _ncCounter;

    public SurfEasyClient(string cacheFilePath, Action<string>? logger = null)
    {
        _cacheFilePath = cacheFilePath;
        _logger = logger;

        var handler = new SocketsHttpHandler
        {
            CookieContainer = _cookies,
            UseCookies = true,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, _, _, _) => true
            }
        };

        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
    }

    public ProxyEndpoint? CurrentEndpoint
    {
        get
        {
            var eps = _state.Endpoints;
            if (eps.Count == 0) return null;
            int idx = Math.Clamp(_currentEndpointIndex, 0, eps.Count - 1);
            return eps[idx];
        }
    }

    public IReadOnlyList<ProxyEndpoint> Endpoints => _state.Endpoints;

    public async Task InitializeAsync(string preferredRegion = "AS", bool forceRefresh = false, CancellationToken ct = default)
    {
        await _initLock.WaitAsync(ct);
        try
        {
            if (!forceRefresh && TryLoadCache())
            {
                Log($"Loaded cached proxy credentials ({_state.Endpoints.Count} endpoints). Verifying fastest node...");
                if (await VerifyAndRankEndpointsAsync(ct))
                {
                    var best = CurrentEndpoint!;
                    Log($"Ready! Connected to upstream node {best.Ip}:{best.Port} [{best.Country}] ({best.LatencyMs}ms).");
                    return;
                }
                Log("Cached endpoints expired or unreachable, registering fresh session...");
            }

            await RegisterAndDiscoverAsync(preferredRegion, ct);
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task RegisterAndDiscoverAsync(string preferredRegion, CancellationToken ct)
    {
        _cookies.GetCookies(new Uri("https://api2.sec-tunnel.com"));
        _state = new CachedProxyState
        {
            DeviceId = RandomCapitalHex(20)
        };

        string localPart = RandomCapitalHex(32);
        _state.SubscriberEmail = $"{localPart}@{ClientType}.best.vpn";
        _state.SubscriberPassword = CapitalHexSha1(_state.SubscriberEmail);

        Log("Registering anonymous tunnel session...");
        using var regSubDoc = await PostRpcAsync($"{ApiBaseUrl}/register_subscriber", new Dictionary<string, string>
        {
            ["email"] = _state.SubscriberEmail,
            ["password"] = _state.SubscriberPassword
        }, ct);
        EnsureStatusOk(regSubDoc, "register_subscriber");

        Log("Registering client device...");
        using var regDevDoc = await PostRpcAsync($"{ApiBaseUrl}/register_device", new Dictionary<string, string>
        {
            ["client_type"] = ClientType,
            ["device_hash"] = _state.DeviceId,
            ["device_name"] = DeviceName
        }, ct);
        EnsureStatusOk(regDevDoc, "register_device");

        var dataElem = regDevDoc.RootElement.GetProperty("data");
        _state.AssignedDeviceId = GetStringOrIntAsString(dataElem.GetProperty("device_id"));
        _state.DevicePassword = dataElem.GetProperty("device_password").GetString() ?? "";
        _state.AssignedDeviceIdHash = CapitalHexSha1(_state.AssignedDeviceId);

        // Try preferred region first (e.g. AS -> Singapore/Asia, then EU -> Europe, then AM -> Americas)
        string[] regionsToQuery = preferredRegion.Equals("AS", StringComparison.OrdinalIgnoreCase)
            ? new[] { "AS", "EU" }
            : new[] { preferredRegion, "AS", "EU" };

        var discovered = new List<ProxyEndpoint>();
        var seenIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var region in regionsToQuery.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                Log($"Discovering proxy nodes for region '{region}'...");
                using var discDoc = await PostRpcAsync($"{ApiBaseUrl}/discover", new Dictionary<string, string>
                {
                    ["serial_no"] = _state.AssignedDeviceIdHash,
                    ["requested_geo"] = $"\"{region}\",,"
                }, ct);

                if (GetReturnCode(discDoc) == 0 &&
                    discDoc.RootElement.TryGetProperty("data", out var discData) &&
                    discData.TryGetProperty("ips", out var ipsElem) &&
                    ipsElem.ValueKind == JsonValueKind.Array)
                {
                    int idx = 0;
                    foreach (var item in ipsElem.EnumerateArray())
                    {
                        string ip = item.TryGetProperty("ip", out var ipProp) ? ipProp.GetString() ?? "" : "";
                        if (string.IsNullOrWhiteSpace(ip) || !seenIps.Add(ip))
                            continue;

                        string cc = region;
                        if (item.TryGetProperty("geo", out var geoProp) &&
                            geoProp.TryGetProperty("country_code", out var ccProp))
                        {
                            cc = ccProp.GetString() ?? region;
                        }

                        // Skip VN if ever returned
                        if (cc.Equals("VN", StringComparison.OrdinalIgnoreCase))
                            continue;

                        int port = 443;
                        if (item.TryGetProperty("ports", out var portsProp) &&
                            portsProp.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var p in portsProp.EnumerateArray())
                            {
                                if (p.TryGetInt32(out int parsedPort) && parsedPort > 0)
                                {
                                    port = parsedPort;
                                    break;
                                }
                            }
                        }

                        string host = item.TryGetProperty("host", out var hostProp) ? hostProp.GetString() ?? "" : "";
                        if (string.IsNullOrWhiteSpace(host))
                        {
                            host = $"{cc.ToLowerInvariant()}{idx}.sec-tunnel.com";
                        }

                        discovered.Add(new ProxyEndpoint
                        {
                            Ip = ip,
                            Port = port,
                            Host = host,
                            Country = cc.ToUpperInvariant()
                        });
                        idx++;
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Region '{region}' discovery warning: {ex.Message}");
            }

            if (discovered.Count >= 4)
                break;
        }

        if (discovered.Count == 0)
            throw new InvalidOperationException("No upstream proxy endpoints returned by discover API.");

        _state.Endpoints = discovered;
        _state.UpdatedAtUtc = DateTime.UtcNow;

        await VerifyAndRankEndpointsAsync(ct);
        SaveCache();

        var best = CurrentEndpoint!;
        Log($"Initialized! Selected fastest upstream: {best.Ip}:{best.Port} [{best.Country}] ({best.LatencyMs}ms).");
    }

    private async Task<bool> VerifyAndRankEndpointsAsync(CancellationToken ct)
    {
        if (_state.Endpoints.Count == 0 ||
            string.IsNullOrEmpty(_state.AssignedDeviceIdHash) ||
            string.IsNullOrEmpty(_state.DevicePassword))
        {
            return false;
        }

        var tasks = _state.Endpoints.Select(async ep =>
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(5));
                await using var stream = await DialSingleEndpointAsync(ep, "api.onstove.com", 443, cts.Token);
                sw.Stop();
                ep.LatencyMs = sw.ElapsedMilliseconds;
                return true;
            }
            catch
            {
                ep.LatencyMs = 9999;
                return false;
            }
        }).ToArray();

        bool[] results = await Task.WhenAll(tasks);
        if (!results.Any(r => r))
            return false;

        _state.Endpoints = _state.Endpoints
            .Where(e => e.LatencyMs < 9000)
            .OrderBy(e => e.LatencyMs)
            .ToList();

        _currentEndpointIndex = 0;
        return _state.Endpoints.Count > 0;
    }

    public async Task<Stream> DialUpstreamAsync(string targetHost, int targetPort, CancellationToken ct = default)
    {
        if (_state.Endpoints.Count == 0)
        {
            await InitializeAsync("AS", false, ct);
        }

        var endpoints = _state.Endpoints;
        Exception? lastError = null;

        for (int attempt = 0; attempt < endpoints.Count; attempt++)
        {
            int idx = (_currentEndpointIndex + attempt) % endpoints.Count;
            var ep = endpoints[idx];
            try
            {
                var stream = await DialSingleEndpointAsync(ep, targetHost, targetPort, ct);
                _currentEndpointIndex = idx;
                return stream;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        // If all cached endpoints failed, force refresh once
        Log("All cached endpoints failed on dial, refreshing session...");
        await InitializeAsync("AS", true, ct);
        return await DialSingleEndpointAsync(CurrentEndpoint!, targetHost, targetPort, ct);
    }

    private async Task<Stream> DialSingleEndpointAsync(ProxyEndpoint ep, string targetHost, int targetPort, CancellationToken ct)
    {
        var tcp = new TcpClient();
        try
        {
            tcp.NoDelay = true;
            await tcp.ConnectAsync(ep.Ip, ep.Port, ct);

            var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false, (_, _, _, _) => true);
            var sslOptions = new SslClientAuthenticationOptions
            {
                TargetHost = string.IsNullOrEmpty(ep.Host) ? "se0.sec-tunnel.com" : ep.Host,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck
            };

            await ssl.AuthenticateAsClientAsync(sslOptions, ct);

            string authRaw = $"{_state.AssignedDeviceIdHash}:{_state.DevicePassword}";
            string authB64 = Convert.ToBase64String(Encoding.ASCII.GetBytes(authRaw));
            string target = $"{targetHost}:{targetPort}";
            string connectReq =
                $"CONNECT {target} HTTP/1.1\r\n" +
                $"Host: {target}\r\n" +
                $"Proxy-Authorization: Basic {authB64}\r\n" +
                $"\r\n";

            byte[] reqBytes = Encoding.ASCII.GetBytes(connectReq);
            await ssl.WriteAsync(reqBytes, ct);
            await ssl.FlushAsync(ct);

            // Read HTTP response headers carefully without consuming tunneled bytes
            string statusLine = await ReadHeaderBlockAsync(ssl, ct);
            if (!statusLine.Contains(" 200 ", StringComparison.Ordinal))
            {
                throw new IOException($"Upstream proxy rejected CONNECT {target}: {statusLine}");
            }

            return ssl;
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    private static async Task<string> ReadHeaderBlockAsync(Stream stream, CancellationToken ct)
    {
        var sb = new StringBuilder(256);
        byte[] oneByte = new byte[1];
        int matchState = 0; // tracks \r\n\r\n

        while (sb.Length < 8192)
        {
            int n = await stream.ReadAsync(oneByte.AsMemory(0, 1), ct);
            if (n == 0)
                throw new EndOfStreamException("Unexpected EOF while reading CONNECT response from upstream proxy.");

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

        string headers = sb.ToString();
        int firstNewLine = headers.IndexOf("\r\n", StringComparison.Ordinal);
        return firstNewLine > 0 ? headers[..firstNewLine] : headers.Trim();
    }

    private async Task<JsonDocument> PostRpcAsync(string url, Dictionary<string, string> formFields, CancellationToken ct)
    {
        var uri = new Uri(url);

        // First request to get Digest challenge (or use challenge response)
        using var req1 = CreateRpcRequest(uri, formFields, null);
        using var resp1 = await _httpClient.SendAsync(req1, ct);

        if (resp1.StatusCode == HttpStatusCode.Unauthorized)
        {
            string? wwwAuth = null;
            if (resp1.Headers.TryGetValues("WWW-Authenticate", out var values))
            {
                wwwAuth = values.FirstOrDefault(v => v.StartsWith("Digest", StringComparison.OrdinalIgnoreCase));
            }

            if (string.IsNullOrEmpty(wwwAuth))
                throw new InvalidOperationException("SurfEasy API returned 401 without Digest challenge.");

            string digestHeader = BuildDigestAuthHeader("POST", uri.PathAndQuery, wwwAuth);
            using var req2 = CreateRpcRequest(uri, formFields, digestHeader);
            using var resp2 = await _httpClient.SendAsync(req2, ct);
            resp2.EnsureSuccessStatusCode();
            string body2 = await resp2.Content.ReadAsStringAsync(ct);
            return JsonDocument.Parse(body2);
        }

        resp1.EnsureSuccessStatusCode();
        string body1 = await resp1.Content.ReadAsStringAsync(ct);
        return JsonDocument.Parse(body1);
    }

    private static HttpRequestMessage CreateRpcRequest(Uri uri, Dictionary<string, string> formFields, string? authorizationHeader)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new FormUrlEncodedContent(formFields)
        };
        req.Headers.TryAddWithoutValidation("SE-Client-Version", ClientVersion);
        req.Headers.TryAddWithoutValidation("SE-Operating-System", OperatingSystemHeader);
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgentHeader);
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        if (!string.IsNullOrEmpty(authorizationHeader))
        {
            req.Headers.TryAddWithoutValidation("Authorization", authorizationHeader);
        }
        return req;
    }

    private string BuildDigestAuthHeader(string method, string digestUri, string wwwAuthenticate)
    {
        var parts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(wwwAuthenticate, @"(\w+)\s*=\s*(?:""([^""]*)""|([^,\s]+))"))
        {
            string key = m.Groups[1].Value;
            string val = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
            parts[key] = val;
        }

        string realm = parts.GetValueOrDefault("realm", "");
        string nonce = parts.GetValueOrDefault("nonce", "");
        string qop = parts.GetValueOrDefault("qop", "auth");
        string opaque = parts.GetValueOrDefault("opaque", "");
        string algorithm = parts.GetValueOrDefault("algorithm", "SHA-256");

        bool useSha256 = algorithm.Contains("256", StringComparison.OrdinalIgnoreCase);
        Func<string, string> hashFunc = useSha256 ? LowerHexSha256 : LowerHexMd5;

        int ncVal = Interlocked.Increment(ref _ncCounter);
        string nc = ncVal.ToString("x8");
        string cnonce = LowerHexRandom(8);

        string ha1 = hashFunc($"{ApiLogin}:{realm}:{ApiSecret}");
        string ha2 = hashFunc($"{method}:{digestUri}");

        string responseHash;
        if (!string.IsNullOrEmpty(qop))
        {
            string qopToken = qop.Split(',')[0].Trim();
            responseHash = hashFunc($"{ha1}:{nonce}:{nc}:{cnonce}:{qopToken}:{ha2}");
            var sb = new StringBuilder();
            sb.Append($"Digest username=\"{ApiLogin}\", realm=\"{realm}\", nonce=\"{nonce}\", uri=\"{digestUri}\", ");
            sb.Append($"algorithm={algorithm}, response=\"{responseHash}\", qop={qopToken}, nc={nc}, cnonce=\"{cnonce}\"");
            if (!string.IsNullOrEmpty(opaque))
                sb.Append($", opaque=\"{opaque}\"");
            return sb.ToString();
        }
        else
        {
            responseHash = hashFunc($"{ha1}:{nonce}:{ha2}");
            var sb = new StringBuilder();
            sb.Append($"Digest username=\"{ApiLogin}\", realm=\"{realm}\", nonce=\"{nonce}\", uri=\"{digestUri}\", ");
            sb.Append($"algorithm={algorithm}, response=\"{responseHash}\"");
            if (!string.IsNullOrEmpty(opaque))
                sb.Append($", opaque=\"{opaque}\"");
            return sb.ToString();
        }
    }

    private static int GetReturnCode(JsonDocument doc)
    {
        if (!doc.RootElement.TryGetProperty("return_code", out var rc))
            return -1;
        if (rc.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in rc.EnumerateObject())
            {
                if (int.TryParse(prop.Name, out int objCode))
                    return objCode;
            }
        }
        if (rc.ValueKind == JsonValueKind.Array && rc.GetArrayLength() > 0)
        {
            var first = rc[0];
            if (first.ValueKind == JsonValueKind.Number && first.TryGetInt32(out int code))
                return code;
            if (first.ValueKind == JsonValueKind.String && int.TryParse(first.GetString(), out int sCode))
                return sCode;
        }
        return -1;
    }

    private static void EnsureStatusOk(JsonDocument doc, string operation)
    {
        int code = GetReturnCode(doc);
        if (code != 0)
        {
            throw new InvalidOperationException($"SurfEasy {operation} failed with return_code={doc.RootElement.GetProperty("return_code")}");
        }
    }

    private static string GetStringOrIntAsString(JsonElement elem)
    {
        return elem.ValueKind switch
        {
            JsonValueKind.String => elem.GetString() ?? "",
            JsonValueKind.Number => elem.GetRawText(),
            _ => elem.ToString()
        };
    }

    private bool TryLoadCache()
    {
        try
        {
            if (!File.Exists(_cacheFilePath))
                return false;
            string json = File.ReadAllText(_cacheFilePath);
            var loaded = JsonSerializer.Deserialize<CachedProxyState>(json);
            if (loaded == null ||
                loaded.Endpoints.Count == 0 ||
                string.IsNullOrEmpty(loaded.AssignedDeviceIdHash) ||
                string.IsNullOrEmpty(loaded.DevicePassword))
            {
                return false;
            }
            if ((DateTime.UtcNow - loaded.UpdatedAtUtc).TotalHours > 12)
                return false;

            _state = loaded;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void SaveCache()
    {
        try
        {
            string json = JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_cacheFilePath, json);
        }
        catch
        {
            // Ignore cache write errors
        }
    }

    private static string RandomCapitalHex(int numBytes)
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(numBytes);
        return Convert.ToHexString(bytes); // Uppercase hex
    }

    private static string LowerHexRandom(int numBytes)
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(numBytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string CapitalHexSha1(string input)
    {
        byte[] hash = SHA1.HashData(Encoding.ASCII.GetBytes(input));
        return Convert.ToHexString(hash); // Uppercase hex
    }

    private static string LowerHexSha256(string input)
    {
        byte[] hash = SHA256.HashData(Encoding.ASCII.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string LowerHexMd5(string input)
    {
        byte[] hash = MD5.HashData(Encoding.ASCII.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private void Log(string msg) => _logger?.Invoke(msg);

    public void Dispose()
    {
        _httpClient.Dispose();
        _initLock.Dispose();
    }
}
