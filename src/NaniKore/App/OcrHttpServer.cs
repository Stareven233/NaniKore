using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using NaniKore.Config;
using NaniKore.Ocr;
using SkiaSharp;

namespace NaniKore.App;

/// <summary>
/// 开发者专用的轻量本地 OCR HTTP 监听服务（issue 讨论决定）：
/// 基于 TcpListener 原生实现极简 HTTP/1.1 协议栈，避开 Windows HttpListener 对 0.0.0.0
/// 与非本地回环地址需要管理员权限注册 URL ACL 的限制。
/// 全链路使用 SkiaSharp 解码与 AOT 静态源生成 JSON。
/// </summary>
internal sealed class OcrHttpServer : IDisposable
{
    private const int MaxHeaderBytes = 64 * 1024;
    private const int MaxBodyBytes = 50 * 1024 * 1024;

    private readonly TcpListener _listener;
    private readonly Func<SKBitmap, OcrResult> _recognizer;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _recognizeGate = new();
    private readonly Task _listenTask;

    public string EndpointDescription { get; }
    public int Port { get; }

    public OcrHttpServer(string listenAddress, Func<SKBitmap, OcrResult> recognizer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(listenAddress);
        _recognizer = recognizer ?? throw new ArgumentNullException(nameof(recognizer));

        var (ipAddress, configuredPort) = ParseEndpoint(listenAddress);
        _listener = new TcpListener(ipAddress, configuredPort);
        _listener.Start();

        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        EndpointDescription = $"{ipAddress}:{Port}";
        _listenTask = Task.Run(ListenLoopAsync);
    }

    private static (IPAddress Address, int Port) ParseEndpoint(string listenAddress)
    {
        listenAddress = listenAddress.Trim();
        var colonIndex = listenAddress.LastIndexOf(':');

        if (colonIndex < 0)
        {
            if (int.TryParse(listenAddress, out var defaultPort))
            {
                return (IPAddress.Loopback, defaultPort);
            }

            throw new FormatException($"监听地址格式无效，应为 'host:port' 或 'port'：{listenAddress}");
        }

        var hostPart = listenAddress[..colonIndex].Trim();
        var portPart = listenAddress[(colonIndex + 1)..].Trim();

        if (!int.TryParse(portPart, out var port) || port < 0 || port > 65535)
        {
            throw new FormatException($"端口号无效：{portPart}");
        }

        if (string.IsNullOrWhiteSpace(hostPart) ||
            string.Equals(hostPart, "0.0.0.0", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(hostPart, "*", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(hostPart, "+", StringComparison.OrdinalIgnoreCase))
        {
            return (IPAddress.Any, port);
        }

        if (string.Equals(hostPart, "localhost", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(hostPart, "127.0.0.1", StringComparison.OrdinalIgnoreCase))
        {
            return (IPAddress.Loopback, port);
        }

        if (IPAddress.TryParse(hostPart, out var parsedIp))
        {
            return (parsedIp, port);
        }

        var entry = Dns.GetHostEntry(hostPart);
        var ip = entry.AddressList.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                 ?? entry.AddressList.FirstOrDefault()
                 ?? throw new ArgumentException($"无法解析的主机地址：{hostPart}");

        return (ip, port);
    }

    private async Task ListenLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                _ = Task.Run(() => HandleClientAsync(client, _cts.Token));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception) when (_cts.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[OcrHttpServer] 接收客户端连接异常：{ex.Message}");
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        await using (var stream = client.GetStream())
        {
            try
            {
                stream.ReadTimeout = 15000;
                stream.WriteTimeout = 15000;

                var (method, path, query, headers, body) = await ReadHttpRequestAsync(stream, ct).ConfigureAwait(false);

                // 路由 1：探活探针
                if (method == "GET" && (path == "/health" || path == "/healthz" || path == "/"))
                {
                    await SendStatusAsync(stream, "ok", ct).ConfigureAwait(false);
                    return;
                }

                // 路由 2：OCR 识别接口
                if (method == "POST" && path == "/ocr")
                {
                    await HandleOcrRequestAsync(stream, query, headers, body, ct).ConfigureAwait(false);
                    return;
                }

                // 其他路由：404
                await SendErrorAsync(stream, 404, "Not Found", $"Not found: {method} {path}", ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 超时或关闭，正常退出
            }
            catch (BadHttpRequestException ex)
            {
                await SendErrorAsync(stream, ex.StatusCode, ex.ReasonPhrase, ex.Message, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[OcrHttpServer] 请求处理异常：{ex}");
                await SendErrorAsync(stream, 500, "Internal Server Error", ex.Message, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleOcrRequestAsync(
        Stream stream,
        Dictionary<string, string> query,
        Dictionary<string, string> headers,
        byte[] body,
        CancellationToken ct)
    {
        // 1. 尝试获取本地文件路径（Query 参数优先，其次尝试 JSON Body 中的 path）
        string? imagePath = null;
        if (query.TryGetValue("path", out var qPath) && !string.IsNullOrWhiteSpace(qPath))
        {
            imagePath = qPath;
        }
        else if (body.Length > 0 && headers.TryGetValue("content-type", out var contentType) &&
                 contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("path", out var pathElem))
                {
                    imagePath = pathElem.GetString();
                }
            }
            catch
            {
                // 非合法 JSON 则保持 imagePath 为 null
            }
        }

        // 2. 加载图像并执行 OCR
        OcrResult result;
        if (!string.IsNullOrWhiteSpace(imagePath))
        {
            var fullPath = Path.GetFullPath(imagePath);
            if (!File.Exists(fullPath))
            {
                await SendErrorAsync(stream, 400, "Bad Request", $"File not found: {fullPath}", ct).ConfigureAwait(false);
                return;
            }

            using var fileImage = SKBitmap.Decode(fullPath);
            if (fileImage is null)
            {
                await SendErrorAsync(stream, 400, "Bad Request", $"Failed to decode image from file: {fullPath}", ct).ConfigureAwait(false);
                return;
            }

            lock (_recognizeGate)
            {
                result = _recognizer(fileImage);
            }
        }
        else if (body.Length > 0)
        {
            using var bodyImage = SKBitmap.Decode(body);
            if (bodyImage is null)
            {
                await SendErrorAsync(stream, 400, "Bad Request", "Failed to decode image from request body.", ct).ConfigureAwait(false);
                return;
            }

            lock (_recognizeGate)
            {
                result = _recognizer(bodyImage);
            }
        }
        else
        {
            await SendErrorAsync(stream, 400, "Bad Request", "No image data or file path provided. Provide binary body or ?path= query.", ct).ConfigureAwait(false);
            return;
        }

        // 3. 返回结构化 JSON
        await SendOcrResponseAsync(stream, result.ToDto(), ct).ConfigureAwait(false);
    }

    private static async Task<(string Method, string Path, Dictionary<string, string> Query, Dictionary<string, string> Headers, byte[] Body)>
        ReadHttpRequestAsync(Stream stream, CancellationToken ct)
    {
        using var headerMs = new MemoryStream();
        var chunk = new byte[4096];
        var delimiterIndex = -1;
        var delimiterLength = 4;

        while (delimiterIndex < 0)
        {
            var bytesRead = await stream.ReadAsync(chunk, ct).ConfigureAwait(false);
            if (bytesRead <= 0)
            {
                throw new BadHttpRequestException(400, "Bad Request", "Client disconnected before sending complete headers.");
            }

            headerMs.Write(chunk, 0, bytesRead);

            var current = headerMs.ToArray();
            delimiterIndex = FindHeaderDelimiter(current, out delimiterLength);

            if (headerMs.Length > MaxHeaderBytes)
            {
                throw new BadHttpRequestException(431, "Request Header Fields Too Large", "Header fields exceeded maximum limit.");
            }
        }

        var fullHeaderBytes = headerMs.ToArray();
        var headerText = Encoding.UTF8.GetString(fullHeaderBytes, 0, delimiterIndex);
        var lines = headerText.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);

        if (lines.Length == 0)
        {
            throw new BadHttpRequestException(400, "Bad Request", "Empty HTTP request line.");
        }

        var requestLineParts = lines[0].Split(' ', 3);
        if (requestLineParts.Length < 2)
        {
            throw new BadHttpRequestException(400, "Bad Request", $"Malformed HTTP request line: {lines[0]}");
        }

        var method = requestLineParts[0].ToUpperInvariant();
        var rawTarget = requestLineParts[1];

        var path = rawTarget;
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var queryIndex = rawTarget.IndexOf('?');
        if (queryIndex >= 0)
        {
            path = rawTarget[..queryIndex];
            var queryString = rawTarget[(queryIndex + 1)..];
            foreach (var segment in queryString.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eqIndex = segment.IndexOf('=');
                if (eqIndex >= 0)
                {
                    var k = WebUtility.UrlDecode(segment[..eqIndex]);
                    var v = WebUtility.UrlDecode(segment[(eqIndex + 1)..]);
                    query[k] = v;
                }
                else
                {
                    query[WebUtility.UrlDecode(segment)] = string.Empty;
                }
            }
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var headerLine = lines[i];
            var colonIdx = headerLine.IndexOf(':');
            if (colonIdx > 0)
            {
                var name = headerLine[..colonIdx].Trim();
                var val = headerLine[(colonIdx + 1)..].Trim();
                headers[name] = val;
            }
        }

        var contentLength = 0;
        if (headers.TryGetValue("content-length", out var clStr) && int.TryParse(clStr, out var cl) && cl > 0)
        {
            if (cl > MaxBodyBytes)
            {
                throw new BadHttpRequestException(413, "Payload Too Large", $"Body size {cl} exceeds maximum allowed {MaxBodyBytes} bytes.");
            }

            contentLength = cl;
        }

        var body = new byte[contentLength];
        if (contentLength > 0)
        {
            var initialBodyOffset = delimiterIndex + delimiterLength;
            var initialBodyAvailable = fullHeaderBytes.Length - initialBodyOffset;
            var copied = Math.Min(initialBodyAvailable, contentLength);

            if (copied > 0)
            {
                Buffer.BlockCopy(fullHeaderBytes, initialBodyOffset, body, 0, copied);
            }

            var remaining = contentLength - copied;
            var offset = copied;
            while (remaining > 0)
            {
                var read = await stream.ReadAsync(body.AsMemory(offset, remaining), ct).ConfigureAwait(false);
                if (read <= 0)
                {
                    throw new BadHttpRequestException(400, "Bad Request", "Unexpected end of stream while reading HTTP body.");
                }

                offset += read;
                remaining -= read;
            }
        }

        return (method, path, query, headers, body);
    }

    private static int FindHeaderDelimiter(byte[] bytes, out int delimiterLength)
    {
        for (var i = 0; i < bytes.Length - 1; i++)
        {
            if (bytes[i] == '\n' && bytes[i + 1] == '\n')
            {
                delimiterLength = 2;
                return i;
            }

            if (i < bytes.Length - 3 &&
                bytes[i] == '\r' && bytes[i + 1] == '\n' &&
                bytes[i + 2] == '\r' && bytes[i + 3] == '\n')
            {
                delimiterLength = 4;
                return i;
            }
        }

        delimiterLength = 0;
        return -1;
    }

    private static async Task SendOcrResponseAsync(Stream stream, OcrResponseDto dto, CancellationToken ct)
    {
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(dto, NaniKoreJsonSerializerContext.Default.OcrResponseDto);
        await SendBytesAsync(stream, 200, "OK", jsonBytes, ct).ConfigureAwait(false);
    }

    private static async Task SendStatusAsync(Stream stream, string status, CancellationToken ct)
    {
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(new OcrStatusDto(status), NaniKoreJsonSerializerContext.Default.OcrStatusDto);
        await SendBytesAsync(stream, 200, "OK", jsonBytes, ct).ConfigureAwait(false);
    }

    private static async Task SendErrorAsync(Stream stream, int statusCode, string reasonPhrase, string error, CancellationToken ct)
    {
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(new OcrErrorDto(error), NaniKoreJsonSerializerContext.Default.OcrErrorDto);
        await SendBytesAsync(stream, statusCode, reasonPhrase, jsonBytes, ct).ConfigureAwait(false);
    }

    private static async Task SendBytesAsync(Stream stream, int statusCode, string reasonPhrase, byte[] jsonBytes, CancellationToken ct)
    {
        try
        {
            var header =
                $"HTTP/1.1 {statusCode} {reasonPhrase}\r\n" +
                $"Content-Type: application/json; charset=utf-8\r\n" +
                $"Content-Length: {jsonBytes.Length}\r\n" +
                $"Connection: close\r\n" +
                $"\r\n";

            var headerBytes = Encoding.ASCII.GetBytes(header);
            await stream.WriteAsync(headerBytes, ct).ConfigureAwait(false);
            await stream.WriteAsync(jsonBytes, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // 客户端断开连接不抛出未处理异常
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _listener.Stop();
        }
        catch
        {
            // 忽略停止异常
        }

        _cts.Dispose();
    }

    private sealed class BadHttpRequestException(int statusCode, string reasonPhrase, string message) : Exception(message)
    {
        public int StatusCode { get; } = statusCode;
        public string ReasonPhrase { get; } = reasonPhrase;
    }
}
