using System.Net.Http;
using System.Net;
using System.Text.Json;

namespace NaniKore.Translation;

internal sealed class ProviderHttpException : Exception
{
    public ProviderHttpException(HttpStatusCode statusCode, string? serverMessage)
        : base(serverMessage ?? $"HTTP {(int)statusCode}")
    {
        StatusCode = statusCode;
        ServerMessage = serverMessage;
    }

    public HttpStatusCode StatusCode { get; }

    public string? ServerMessage { get; }

    public static async Task<ProviderHttpException> CreateAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        string? message = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var error))
            {
                message = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var nested)
                    ? nested.GetString()
                    : error.ToString();
            }
            else if (root.TryGetProperty("message", out var direct))
            {
                message = direct.GetString();
            }
        }
        catch (JsonException)
        {
            message = body.Length <= 500 ? body : body[..500];
        }

        return new ProviderHttpException(response.StatusCode, message);
    }
}

/// <summary>将服务端状态映射为用户可行动的中文提示，避免把原始协议异常直接扔给面板。</summary>
internal static class ProviderErrorMapper
{
    public static string ToUserMessage(Exception exception, string? profileName = null, string? model = null, string? baseUrl = null)
    {
        if (exception is ProviderCapabilityException or ProviderConfigurationException or ProviderTransportPolicyException or ProviderTimeoutException)
        {
            // 这些异常自带可行动的中文说明（含具体原因与调大 timeout_seconds 的建议），原样透出。
            return exception.Message;
        }

        if (exception is ProviderHttpException http)
        {
            var detail = http.ServerMessage ?? string.Empty;
            return http.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "API Key 无效或没有访问权限，请在设置中检查该配置档的密钥",
                HttpStatusCode.NotFound => $"找不到服务或模型。请检查配置档“{profileName ?? "当前"}”的模型“{model ?? ""}”与 Base URL“{baseUrl ?? ""}”",
                (HttpStatusCode)429 => "请求受到限流，请稍后重试或切换配置档",
                HttpStatusCode.BadRequest or HttpStatusCode.InternalServerError when ContainsVisionHint(detail) =>
                    "当前路由的模型未启用图像输入（多模态）。请在上游启用 multimodal processing，或切换支持视觉的模型",
                HttpStatusCode.BadRequest => string.IsNullOrWhiteSpace(detail) ? "请求参数被服务端拒绝" : detail,
                _ => string.IsNullOrWhiteSpace(detail) ? $"服务端返回 HTTP {(int)http.StatusCode}" : detail,
            };
        }

        return exception switch
        {
            TaskCanceledException => "请求已超时；若网络正常，请在设置中调大 timeout_seconds",
            HttpRequestException => "无法连接到翻译服务，请检查网络和 Base URL",
            IOException => "翻译输出在传输中中断，已保留收到的部分内容",
            _ => $"翻译失败：{exception.Message}",
        };
    }

    private static bool ContainsVisionHint(string message) =>
        message.Contains("image", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("vision", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("multimodal", StringComparison.OrdinalIgnoreCase);
}

