namespace NaniKore.Favorites;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

/// <summary>
/// 收藏条目的统一数据模型。
/// 与切片 5 SQLite 的 favorites 表 Schema 保持严格一致，支持无缝落库。
/// </summary>
public sealed class FavoriteItem
{
    /// <summary>数据库主键自增 ID（内存态默认为 0）。</summary>
    public long Id { get; set; }

    /// <summary>类别标签：'单词' | '短语' | '语法' | '备注'。</summary>
    public string Category { get; set; } = "单词";

    /// <summary>核心显示文本（生词、句型原形或划选文字）。</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// 纯文本分层标注说明（免 Markdown 解析开销，分行易读）。
    /// 精讲卡片收藏时包含读音、原形、释义；划词收藏时由用户自选或留空。
    /// </summary>
    public string? Note { get; set; }

    /// <summary>收藏创建时间的 Unix 毫秒时间戳或本地时间。</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}

/// <summary>
/// 知识点与生词收藏服务的核心契约。
/// </summary>
public interface IFavoriteService
{
    /// <summary>新增一条收藏。</summary>
    Task<bool> AddFavoriteAsync(FavoriteItem item);

    /// <summary>移除一条收藏（按核心内容匹配）。</summary>
    Task<bool> RemoveFavoriteAsync(string content);

    /// <summary>根据字符串内容快速判断是否已经收藏过（支持不区分大小写及首尾空白）。</summary>
    bool IsFavorited(string content, string? category = null);

    /// <summary>获取当前所有已收藏条目（快照）。</summary>
    IReadOnlyList<FavoriteItem> GetAllFavorites();

    /// <summary>条目被成功收藏时激发的事件。</summary>
    event EventHandler<FavoriteItem>? FavoriteAdded;

    /// <summary>条目被移除收藏时激发的事件。</summary>
    event EventHandler<string>? FavoriteRemoved;
}

/// <summary>
/// 内存态轻量收藏服务实现（切片 4 默认实现，为切片 5 SQLite 提供前置契约与平滑替换能力）。
/// </summary>
public class InMemoryFavoriteService : IFavoriteService
{
    private readonly object _syncRoot = new();
    private readonly Dictionary<string, FavoriteItem> _items = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler<FavoriteItem>? FavoriteAdded;
    public event EventHandler<string>? FavoriteRemoved;

    public Task<bool> AddFavoriteAsync(FavoriteItem item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.Content))
        {
            return Task.FromResult(false);
        }

        var key = item.Content.Trim();
        lock (_syncRoot)
        {
            _items[key] = item;
        }

        FavoriteAdded?.Invoke(this, item);
        return Task.FromResult(true);
    }

    public Task<bool> RemoveFavoriteAsync(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return Task.FromResult(false);
        }

        var key = content.Trim();
        bool removed;
        lock (_syncRoot)
        {
            removed = _items.Remove(key);
        }

        if (removed)
        {
            FavoriteRemoved?.Invoke(this, key);
        }

        return Task.FromResult(removed);
    }

    public bool IsFavorited(string content, string? category = null)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        var key = content.Trim();
        lock (_syncRoot)
        {
            if (_items.TryGetValue(key, out var item))
            {
                if (string.IsNullOrEmpty(category) || string.Equals(item.Category, category, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public IReadOnlyList<FavoriteItem> GetAllFavorites()
    {
        lock (_syncRoot)
        {
            return _items.Values.OrderByDescending(x => x.CreatedAt).ToList();
        }
    }
}

