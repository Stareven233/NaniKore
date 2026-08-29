using System.Net.Http;
using System.Text.Json;
using NaniKore.Config;

namespace NaniKore.Translation;

/// <summary>记录翻译链路的安全诊断信息；不落盘密钥、图片、原文或成功响应内容，失败时保留限长服务端错误文本。</summary>
internal static class TranslationDiagnostics
{
    private static readonly object SyncRoot = new();
    private static readonly HttpRequestOptionsKey<string> RequestIdKey = new("NaniKore.TranslationRequestId");
    private static readonly string LogDirectory = Path.Combine(AppContext.BaseDirectory, "logs");

    public static string LogFilePath => Path.Combine(LogDirectory, "translation-diagnostics.log");

    public static string RequestStarted(ProviderProfile profile, HttpRequestMessage request)
    {
        var requestId = Guid.NewGuid().ToString("N");
        request.Options.Set(RequestIdKey, requestId);
        Write("request_started", new
        {
            request_id = requestId,
            provider = profile.Kind,
            profile = profile.Id,
            method = request.Method.Method,
            endpoint = DescribeEndpoint(request.RequestUri),
            content_type = request.Content?.Headers.ContentType?.MediaType,
        });
        return requestId;
    }

    public static void RequestFailed(ProviderProfile profile, string requestId, Exception exception) =>
        Write("request_failed", new
        {
            request_id = requestId,
            provider = profile.Kind,
            profile = profile.Id,
            exception = exception.GetType().Name,
        });

    public static void ResponseReceived(ProviderProfile profile, string requestId, HttpResponseMessage response) =>
        Write("response_received", new
        {
            request_id = requestId,
            provider = profile.Kind,
            profile = profile.Id,
            status = (int)response.StatusCode,
            content_type = response.Content.Headers.ContentType?.MediaType,
            content_length = response.Content.Headers.ContentLength,
        });

    public static void ResponseRejected(
        ProviderProfile profile,
        string requestId,
        HttpResponseMessage response,
        ProviderHttpException exception) =>
        Write("response_rejected", new
        {
            request_id = requestId,
            provider = profile.Kind,
            profile = profile.Id,
            status = (int)response.StatusCode,
            content_type = response.Content.Headers.ContentType?.MediaType,
            content_length = response.Content.Headers.ContentLength,
            server_message = LimitErrorMessage(exception.ServerMessage),
        });

    public static string GetRequestId(HttpResponseMessage response) =>
        response.RequestMessage?.Options.TryGetValue(RequestIdKey, out string? requestId) == true
            ? requestId
            : "unknown";

    internal static string? LimitErrorMessage(string? message) =>
        string.IsNullOrWhiteSpace(message)
            ? null
            : message.Length <= 2_000 ? message : message[..2_000] + "…";

    public static void ResponseFrame(
        ProviderProfile profile,
        string requestId,
        string protocol,
        string? eventName,
        string data,
        int extractedTextLength)
    {
        var shape = DescribeJson(data);
        Write("response_frame", new
        {
            request_id = requestId,
            provider = profile.Kind,
            profile = profile.Id,
            protocol,
            event_name = eventName,
            json_valid = shape.IsValid,
            json_paths = shape.Paths,
            extracted_text_length = extractedTextLength,
            is_done = string.Equals(data, "[DONE]", StringComparison.Ordinal),
        });
    }

    public static void ResponseStreamCompleted(
        ProviderProfile profile,
        string requestId,
        string protocol,
        int frameCount,
        int extractedTextLength) =>
        Write("response_stream_completed", new
        {
            request_id = requestId,
            provider = profile.Kind,
            profile = profile.Id,
            protocol,
            frame_count = frameCount,
            extracted_text_length = extractedTextLength,
        });

    internal static (bool IsValid, IReadOnlyList<string> Paths) DescribeJson(string data)
    {
        if (string.Equals(data, "[DONE]", StringComparison.Ordinal))
        {
            return (true, []);
        }

        try
        {
            using var document = JsonDocument.Parse(data);
            var paths = new List<string>();
            CollectPaths(document.RootElement, string.Empty, paths, depth: 0);
            return (true, paths);
        }
        catch (JsonException)
        {
            return (false, []);
        }
    }

    private static void CollectPaths(JsonElement value, string prefix, List<string> paths, int depth)
    {
        if (depth > 8 || paths.Count >= 128)
        {
            return;
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                var path = string.IsNullOrEmpty(prefix) ? property.Name : $"{prefix}.{property.Name}";
                paths.Add(path);
                CollectPaths(property.Value, path, paths, depth + 1);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray().Take(8))
            {
                CollectPaths(item, prefix + "[]", paths, depth + 1);
            }
        }
    }

    private static string DescribeEndpoint(Uri? uri)
    {
        if (uri is null)
        {
            return string.Empty;
        }

        // 只保留协议、主机、端口和路径，避免将查询参数中的凭据写入日志。
        var port = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
        return $"{uri.Scheme}://{uri.Host}{port}{uri.AbsolutePath}";
    }

    private static void Write(string eventName, object details)
    {
        try
        {
            var entry = new
            {
                timestamp_utc = DateTimeOffset.UtcNow,
                event_name = eventName,
                details,
            };
            var line = JsonSerializer.Serialize(entry) + Environment.NewLine;
            lock (SyncRoot)
            {
                Directory.CreateDirectory(LogDirectory);
                File.AppendAllText(LogFilePath, line);
            }
        }
        catch
        {
            // 诊断日志失败不能影响翻译主流程。
        }
    }
}
