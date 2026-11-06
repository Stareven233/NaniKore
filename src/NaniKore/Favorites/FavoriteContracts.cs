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
    /// 纯文本备注：每行一项（如读音、词性原形、释义），不写「读音：」这类字段前缀，
    /// 避免收藏页关键词搜索命中无意义提示词。精讲卡片收藏时自动生成，划词收藏可留空。
    /// </summary>
    public string? Note { get; set; }

    /// <summary>收藏创建时间的 Unix 毫秒时间戳或本地时间。</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>
    /// 收藏条目语言：取值 = 共享语言列表的母语写法展示名（日本語 / English / 中文（简体）…）。
    /// 旧库行由 2.3-B 的 ALTER 回填「日本語」；新收藏一律由调用方显式赋值，不依赖列默认。
    /// </summary>
    public string Language { get; set; } = "日本語";
}

/// <summary>
/// 知识点与生词收藏服务的核心契约。
/// </summary>
public interface IFavoriteService
{
    /// <summary>新增一条收藏。</summary>
    Task<bool> AddFavoriteAsync(FavoriteItem item);

    /// <summary>
    /// 按主键 Id 更新收藏条目的 category / content / note / language（created_at 保持原值）。
    /// content 更新按与 Add 相同的归一化函数落库；若归一化后命中另一行（Id 不同）则拒绝并返回 false，
    /// 不静默合并、不覆盖（issue 015 / 2.5 冲突策略）。
    /// </summary>
    Task<bool> UpdateFavoriteAsync(FavoriteItem item);

    /// <summary>移除一条收藏（按核心内容匹配）。</summary>
    Task<bool> RemoveFavoriteAsync(string content);

    /// <summary>根据字符串内容快速判断是否已经收藏过（支持不区分大小写及首尾空白）。</summary>
    bool IsFavorited(string content, string? category = null);

    /// <summary>获取当前所有已收藏条目（快照）。</summary>
    IReadOnlyList<FavoriteItem> GetAllFavorites();

    /// <summary>条目被成功收藏时激发的事件。</summary>
    event EventHandler<FavoriteItem>? FavoriteAdded;

    /// <summary>条目被成功移除收藏时激发的事件。</summary>
    event EventHandler<string>? FavoriteRemoved;

    /// <summary>条目被就地编辑保存成功后激发的事件（携带更新后的条目）。</summary>
    event EventHandler<FavoriteItem>? FavoriteUpdated;
}

/// <summary>
/// 内存态轻量收藏服务实现（切片 4 默认实现，为切片 5 SQLite 提供前置契约与平滑替换能力）。
/// 键为 <see cref="FavoriteContentNormalizer.Normalize"/> 归一化后的内容：
/// 卡片、划词气泡与历史脏行（気付く[きづく] / 気付く (きづく)）在此收敛到同一行，不产生重复收藏。
/// </summary>
public class InMemoryFavoriteService : IFavoriteService
{
    private readonly object _syncRoot = new();
    private readonly Dictionary<string, FavoriteItem> _items = new(StringComparer.OrdinalIgnoreCase);
    private long _nextId = 1;

    public event EventHandler<FavoriteItem>? FavoriteAdded;
    public event EventHandler<string>? FavoriteRemoved;
    public event EventHandler<FavoriteItem>? FavoriteUpdated;

    public Task<bool> AddFavoriteAsync(FavoriteItem item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.Content))
        {
            return Task.FromResult(false);
        }

        var key = FavoriteContentNormalizer.Normalize(item.Content);
        if (key.Length == 0)
        {
            return Task.FromResult(false);
        }

        lock (_syncRoot)
        {
            // 内存态补发自增 Id，让 UpdateFavoriteAsync 有与 SQLite 一致的主键语义。
            if (item.Id == 0)
            {
                item.Id = _nextId++;
            }

            _items[key] = item;
        }

        // 入库即归一化：调用方随后读到的 Content 已是规范形态，与 SQLite 实现语义一致。
        item.Content = key;
        FavoriteAdded?.Invoke(this, item);
        return Task.FromResult(true);
    }

    public Task<bool> UpdateFavoriteAsync(FavoriteItem item)
    {
        if (item is null || item.Id <= 0 || string.IsNullOrWhiteSpace(item.Content))
        {
            return Task.FromResult(false);
        }

        var key = FavoriteContentNormalizer.Normalize(item.Content);
        if (key.Length == 0)
        {
            return Task.FromResult(false);
        }

        lock (_syncRoot)
        {
            FavoriteItem? existing = null;
            var oldKey = string.Empty;
            foreach (var pair in _items)
            {
                if (pair.Value.Id == item.Id)
                {
                    existing = pair.Value;
                    oldKey = pair.Key;
                    break;
                }
            }

            if (existing is null)
            {
                return Task.FromResult(false);
            }

            // 归一化冲突：新内容命中另一行（Id 不同）时拒绝，不静默合并。
            if (!string.Equals(oldKey, key, StringComparison.OrdinalIgnoreCase) && _items.ContainsKey(key))
            {
                return Task.FromResult(false);
            }

            if (!string.Equals(oldKey, key, StringComparison.OrdinalIgnoreCase))
            {
                _items.Remove(oldKey);
            }

            // 保留原 created_at，只更新可编辑字段。
            var updated = new FavoriteItem
            {
                Id = existing.Id,
                Category = string.IsNullOrWhiteSpace(item.Category) ? "单词" : item.Category.Trim(),
                Content = key,
                Note = string.IsNullOrWhiteSpace(item.Note) ? null : item.Note,
                CreatedAt = existing.CreatedAt,
                Language = string.IsNullOrWhiteSpace(item.Language) ? "日本語" : item.Language.Trim(),
            };
            _items[key] = updated;
            item.Category = updated.Category;
            item.Content = updated.Content;
            item.Note = updated.Note;
            item.Language = updated.Language;
            item.CreatedAt = updated.CreatedAt;

            FavoriteUpdated?.Invoke(this, updated);
            return Task.FromResult(true);
        }
    }

    public Task<bool> RemoveFavoriteAsync(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return Task.FromResult(false);
        }

        var key = FavoriteContentNormalizer.Normalize(content);
        bool removed = false;
        string? stored = null;
        lock (_syncRoot)
        {
            if (_items.TryGetValue(key, out var item))
            {
                stored = item.Content;
                removed = _items.Remove(key);
            }
        }

        if (removed)
        {
            // 事件回传库内实际形态（可能与入参不同，如旧脏行 気付く[きづく] 被 気付く 点掉）。
            FavoriteRemoved?.Invoke(this, stored ?? key);
        }

        return Task.FromResult(removed);
    }

    public bool IsFavorited(string content, string? category = null)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        var key = FavoriteContentNormalizer.Normalize(content);
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

