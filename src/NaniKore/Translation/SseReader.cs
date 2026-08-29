using System.Runtime.CompilerServices;
using System.Text;

namespace NaniKore.Translation;

internal readonly record struct SseEvent(string? Event, string Data);

/// <summary>按 SSE 行协议聚合事件；StreamReader 负责处理 data: 行跨网络包的情况。</summary>
internal static class SseReader
{
    public static async IAsyncEnumerable<SseEvent> ReadAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var eventName = (string?)null;
        var data = new StringBuilder();
        var plainData = new StringBuilder();

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    if (data[^1] == '\n')
                    {
                        data.Length--;
                    }

                    yield return new SseEvent(eventName, data.ToString());
                }
                else if (plainData.Length > 0)
                {
                    // 部分视觉端点会忽略 stream=true，直接返回一个普通 JSON 响应。
                    yield return new SseEvent(null, plainData.ToString());
                }

                eventName = null;
                data.Clear();
                plainData.Clear();
                continue;
            }

            if (line[0] == ':')
            {
                continue;
            }

            if (plainData.Length > 0 || IsJsonStart(line))
            {
                plainData.Append(line);
                continue;
            }

            var separator = line.IndexOf(':');
            if (separator < 0)
            {
                continue;
            }

            var field = line[..separator];
            var value = line[(separator + 1)..].TrimStart(' ');
            switch (field)
            {
                case "event":
                    eventName = value;
                    break;
                case "data":
                    data.Append(value).Append('\n');
                    break;
            }
        }

        if (data.Length > 0)
        {
            if (data[^1] == '\n')
            {
                data.Length--;
            }

            yield return new SseEvent(eventName, data.ToString());
        }
        else if (plainData.Length > 0)
        {
            yield return new SseEvent(null, plainData.ToString());
        }
    }

    private static bool IsJsonStart(string line) =>
        line.TrimStart().StartsWith('{') || line.TrimStart().StartsWith('[');
}
