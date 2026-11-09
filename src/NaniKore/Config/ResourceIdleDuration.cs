using System.Globalization;

namespace NaniKore.Config;

/// <summary>本地资源闲置时长的统一语法；零时长表示永不回收，由调用方解释。</summary>
internal static class ResourceIdleDuration
{
    public static bool TryParse(string? value, out TimeSpan timeout)
    {
        timeout = TimeSpan.Zero;
        var text = value.AsSpan().Trim();
        if (text.IsEmpty)
        {
            return false;
        }

        var ticksPerUnit = TimeSpan.TicksPerSecond;
        switch (text[^1])
        {
            case 's' or 'S':
                text = text[..^1];
                break;
            case 'm' or 'M':
                ticksPerUnit = TimeSpan.TicksPerMinute;
                text = text[..^1];
                break;
            case 'h' or 'H':
                ticksPerUnit = TimeSpan.TicksPerHour;
                text = text[..^1];
                break;
        }

        // 显式限定 ASCII 数字，排除解析库可能容忍的尾随空字符等非整数语法。
        foreach (var digit in text)
        {
            if (digit is < '0' or > '9')
            {
                return false;
            }
        }

        // 只接受十进制整数；先除后比较，避免单位换算溢出或浮点取整越过 TimeSpan 上限。
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var amount) ||
            amount < 0 || amount > TimeSpan.MaxValue.Ticks / ticksPerUnit)
        {
            return false;
        }

        timeout = TimeSpan.FromTicks(amount * ticksPerUnit);
        return true;
    }
}
