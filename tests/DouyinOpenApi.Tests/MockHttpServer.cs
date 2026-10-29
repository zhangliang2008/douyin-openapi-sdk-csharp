using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DouyinOpenApi.Tests;

/// <summary>
/// 极简 HTTP 服务端，仅用于测试：捕获 SDK 发出的真实请求，并按队列返回预设响应。
/// <para>
/// 为什么不用 HttpListener：Windows 上 HttpListener 绑定端口需要 URL ACL（管理员权限），
/// 会让测试在 CI 或普通用户下直接失败。这里用 TcpListener 手撸 HTTP/1.1，
/// 只实现测试需要的部分（Content-Length 定长的请求体），零权限依赖。
/// </para>
/// </summary>
internal sealed class MockHttpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly Thread _acceptThread;
    private readonly ConcurrentQueue<(int Status, string Body)> _responses = new();
    private readonly List<CapturedRequest> _requests = new();
    private readonly object _sync = new();
    private volatile bool _disposed;

    /// <summary>监听端口（由系统分配）。</summary>
    public int Port { get; }

    /// <summary>可直接用于 HostOverrides 的基地址。</summary>
    public string BaseUrl => $"http://127.0.0.1:{Port}";

    /// <summary>已收到的请求快照。</summary>
    public IReadOnlyList<CapturedRequest> Requests
    {
        get { lock (_sync) return _requests.ToArray(); }
    }

    /// <summary>已收到的请求数量。</summary>
    public int RequestCount
    {
        get { lock (_sync) return _requests.Count; }
    }

    public MockHttpServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _acceptThread = new Thread(AcceptLoop) { IsBackground = true };
        _acceptThread.Start();
    }

    /// <summary>入队一个响应，按请求到达顺序消费。</summary>
    public void Enqueue(string body, int status = 200) => _responses.Enqueue((status, body));

    /// <summary>连续入队 n 个相同响应。</summary>
    public void EnqueueTimes(int count, string body, int status = 200)
    {
        for (var i = 0; i < count; i++) Enqueue(body, status);
    }

    private void AcceptLoop()
    {
        while (!_disposed)
        {
            TcpClient client;
            try
            {
                client = _listener.AcceptTcpClient();
            }
            catch (SocketException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            try
            {
                Handle(client);
            }
            catch (Exception ex)
            {
                // 测试期间的解析失败不应拖垮整个服务端，记录下来便于定位
                Console.WriteLine("[MockHttpServer] 处理请求失败: " + ex.Message);
            }
        }
    }

    private void Handle(TcpClient client)
    {
        using (client)
        using (var stream = client.GetStream())
        {
            stream.ReadTimeout = 10000;

            // ---- 逐字节读取请求头，直到遇到 CRLFCRLF ----
            var headerBytes = new List<byte>(1024);
            var one = new byte[1];
            while (true)
            {
                var n = stream.Read(one, 0, 1);
                if (n <= 0) return;
                headerBytes.Add(one[0]);

                if (headerBytes.Count >= 4
                    && headerBytes[headerBytes.Count - 4] == (byte)'\r'
                    && headerBytes[headerBytes.Count - 3] == (byte)'\n'
                    && headerBytes[headerBytes.Count - 2] == (byte)'\r'
                    && headerBytes[headerBytes.Count - 1] == (byte)'\n')
                {
                    break;
                }
            }

            var headerText = Encoding.ASCII.GetString(headerBytes.ToArray());
            var lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
            var requestLine = lines[0].Split(' ');

            var captured = new CapturedRequest
            {
                Method = requestLine.Length > 0 ? requestLine[0] : string.Empty,
                RawUrl = requestLine.Length > 1 ? requestLine[1] : string.Empty,
            };

            for (var i = 1; i < lines.Length; i++)
            {
                if (string.IsNullOrEmpty(lines[i])) continue;
                var idx = lines[i].IndexOf(':');
                if (idx <= 0) continue;
                // 注意：这里不用 C# 8 的区间语法（[..idx]），因为 net472 没有 System.Index/Range
                captured.Headers[lines[i].Substring(0, idx).Trim()] = lines[i].Substring(idx + 1).Trim();
            }

            // ---- 按 Content-Length 读取请求体 ----
            var contentLength = 0;
            if (captured.Headers.TryGetValue("Content-Length", out var cl))
                int.TryParse(cl, out contentLength);

            if (contentLength > 0)
            {
                var body = new byte[contentLength];
                var offset = 0;
                while (offset < contentLength)
                {
                    var n = stream.Read(body, offset, contentLength - offset);
                    if (n <= 0) break;
                    offset += n;
                }
                captured.BodyBytes = body;
            }

            lock (_sync) _requests.Add(captured);

            var (status, responseBody) = _responses.TryDequeue(out var preset)
                ? preset
                : (200, "{\"err_no\":0,\"data\":{}}");

            var payload = Encoding.UTF8.GetBytes(responseBody);
            var head = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\n"
                       + "Content-Type: application/json\r\n"
                       + $"Content-Length: {payload.Length}\r\n"
                       + "Connection: close\r\n\r\n";
            var headBytes = Encoding.ASCII.GetBytes(head);
            stream.Write(headBytes, 0, headBytes.Length);
            stream.Write(payload, 0, payload.Length);
            stream.Flush();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        try
        {
            _listener.Stop();
        }
        catch
        {
            // 关闭阶段的异常不影响测试结论
        }
    }
}

/// <summary>服务端捕获到的一次请求。</summary>
internal sealed class CapturedRequest
{
    public string Method { get; set; } = string.Empty;

    /// <summary>请求行里的原始 URL（含查询串）。</summary>
    public string RawUrl { get; set; } = string.Empty;

    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

    public byte[] BodyBytes { get; set; } = Array.Empty<byte>();

    public string Body => Encoding.UTF8.GetString(BodyBytes);

    /// <summary>URL 中的路径部分（不含查询串）。</summary>
    public string Path => RawUrl.Split('?')[0];

    /// <summary>URL 中的查询串部分（不含 <c>?</c>）。</summary>
    public string Query
    {
        get
        {
            var idx = RawUrl.IndexOf('?');
            return idx < 0 ? string.Empty : RawUrl.Substring(idx + 1);
        }
    }

    /// <summary>把查询串解析成字典，便于断言。</summary>
    public Dictionary<string, string> QueryParams
    {
        get
        {
            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in Query.Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var idx = pair.IndexOf('=');
                var key = idx < 0 ? pair : pair.Substring(0, idx);
                var value = idx < 0 ? string.Empty : pair.Substring(idx + 1);
                dict[Uri.UnescapeDataString(key)] = Uri.UnescapeDataString(value.Replace("+", " "));
            }
            return dict;
        }
    }
}
