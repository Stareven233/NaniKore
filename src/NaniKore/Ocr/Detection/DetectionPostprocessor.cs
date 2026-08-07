using NaniKore.Ocr.Models;

namespace NaniKore.Ocr.Detection;

internal static class DetectionPostprocessor
{
    public static IReadOnlyList<DetectionBox> Extract(
        ReadOnlySpan<float> probabilityMap,
        int mapWidth,
        int mapHeight,
        Size sourceSize,
        DetectorConfig config)
    {
        if (probabilityMap.Length != checked(mapWidth * mapHeight))
        {
            throw new ArgumentException("Detection probability map size does not match its shape.", nameof(probabilityMap));
        }

        var visited = new byte[probabilityMap.Length];
        var pending = new List<int>(256);
        var boxes = new List<DetectionBox>();
        var examinedCandidates = 0;

        for (var index = 0; index < probabilityMap.Length; index++)
        {
            if (visited[index] != 0 || probabilityMap[index] < config.Threshold)
            {
                continue;
            }

            examinedCandidates++;
            if (examinedCandidates > config.MaxCandidates)
            {
                break;
            }

            var component = FloodFill(probabilityMap, mapWidth, mapHeight, index, config.Threshold, visited, pending);
            if (component.Width < 3 || component.Height < 3 || component.Score < config.BoxThreshold)
            {
                continue;
            }

            var expanded = Expand(component.Bounds, component.PixelCount, config.UnclipRatio, mapWidth, mapHeight);
            var sourceBounds = MapToSource(expanded, mapWidth, mapHeight, sourceSize);
            if (sourceBounds.Width >= 3 && sourceBounds.Height >= 3)
            {
                boxes.Add(new DetectionBox(sourceBounds, component.Score));
            }
        }

        return boxes;
    }

    private static Component FloodFill(
        ReadOnlySpan<float> map,
        int width,
        int height,
        int start,
        float threshold,
        byte[] visited,
        List<int> pending)
    {
        pending.Clear();
        pending.Add(start);
        visited[start] = 1;

        var minimumX = width;
        var minimumY = height;
        var maximumX = 0;
        var maximumY = 0;
        var pixelCount = 0;
        double scoreSum = 0;

        while (pending.Count > 0)
        {
            var last = pending.Count - 1;
            var index = pending[last];
            pending.RemoveAt(last);

            var y = Math.DivRem(index, width, out var x);
            minimumX = Math.Min(minimumX, x);
            minimumY = Math.Min(minimumY, y);
            maximumX = Math.Max(maximumX, x);
            maximumY = Math.Max(maximumY, y);
            pixelCount++;
            scoreSum += map[index];

            var top = Math.Max(0, y - 1);
            var bottom = Math.Min(height - 1, y + 1);
            var left = Math.Max(0, x - 1);
            var right = Math.Min(width - 1, x + 1);
            for (var neighborY = top; neighborY <= bottom; neighborY++)
            {
                for (var neighborX = left; neighborX <= right; neighborX++)
                {
                    var neighbor = neighborY * width + neighborX;
                    if (visited[neighbor] != 0 || map[neighbor] < threshold)
                    {
                        continue;
                    }

                    visited[neighbor] = 1;
                    pending.Add(neighbor);
                }
            }
        }

        var bounds = Rectangle.FromLTRB(minimumX, minimumY, maximumX + 1, maximumY + 1);
        return new Component(bounds, pixelCount, (float)(scoreSum / pixelCount));
    }

    private static Rectangle Expand(
        Rectangle bounds,
        int componentArea,
        float unclipRatio,
        int mapWidth,
        int mapHeight)
    {
        // 官方 unclip 使用多边形面积/周长计算外扩距离；轻量实现以连通域外接矩形近似，
        // 避免为第一版引入体积明显的 OpenCV 原生依赖。旋转/弯曲文本会弱于官方后处理。
        var perimeter = Math.Max(1, 2 * (bounds.Width + bounds.Height));
        var distance = Math.Max(1, (int)Math.Ceiling(componentArea * unclipRatio / perimeter));
        return Rectangle.FromLTRB(
            Math.Max(0, bounds.Left - distance),
            Math.Max(0, bounds.Top - distance),
            Math.Min(mapWidth, bounds.Right + distance),
            Math.Min(mapHeight, bounds.Bottom + distance));
    }

    private static Rectangle MapToSource(Rectangle bounds, int mapWidth, int mapHeight, Size source)
    {
        var left = (int)Math.Floor(bounds.Left * source.Width / (double)mapWidth);
        var top = (int)Math.Floor(bounds.Top * source.Height / (double)mapHeight);
        var right = (int)Math.Ceiling(bounds.Right * source.Width / (double)mapWidth);
        var bottom = (int)Math.Ceiling(bounds.Bottom * source.Height / (double)mapHeight);
        return Rectangle.FromLTRB(
            Math.Clamp(left, 0, source.Width),
            Math.Clamp(top, 0, source.Height),
            Math.Clamp(right, 0, source.Width),
            Math.Clamp(bottom, 0, source.Height));
    }

    private sealed record Component(Rectangle Bounds, int PixelCount, float Score)
    {
        public int Width => Bounds.Width;
        public int Height => Bounds.Height;
    }
}

internal sealed record DetectionBox(Rectangle Bounds, float Confidence);