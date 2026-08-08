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
        string? eventName = null;
        var data = new StringBuilder();

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

                eventName = null;
                data.Clear();
                continue;
            }

            if (line[0] == ':')
            {
                continue;
            }

            var separator = line.IndexOf(':');
            var field = separator < 0 ? line : line[..separator];
            var value = separator < 0 ? string.Empty : line[(separator + 1)..].TrimStart(' ');
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
    }
}
