namespace NaniKore.Favorites;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using NaniKore.Config;

/// <summary>
/// 基于 SQLite 的持久化收藏服务（切片 5）。
/// 数据库文件固定落在数据目录 <c>程序目录\.data\collections.db</c>（issue 017：本机数据全部收拢进 .data），
/// 绿色便携、覆盖升级不丢数据；采用惰性初始化：未发生任何收藏前不创建库文件，删除库文件后按空态继续工作。
/// 与 <see cref="InMemoryFavoriteService"/> 保持完全一致的操作语义（同内容大小写不敏感 upsert），
/// 保证切片 4 的星标同步逻辑无需任何改动即可切换到持久化实现。
/// SQLite 本地读写为毫秒级同步操作，接口的 Task 返回以已完成任务透出，避免无谓的线程切换。
/// </summary>
public sealed class SqliteFavoriteService : IFavoriteService
{
    /// <summary>数据库文件名：与 exe 的 .data 数据目录同级，随应用整体拷贝/备份。</summary>
    public const string DatabaseFileName = "collections.db";

    private readonly string _databasePath;
    private readonly string _connectionString;

    public SqliteFavoriteService(string? databasePath = null)
    {
        // 默认路径取数据目录（.data）；测试可注入临时路径以获得确定性隔离。
        _databasePath = databasePath ?? DefaultDatabasePath;
        // 显式关闭连接池：桌面单用户场景没有复用收益，而池化句柄会在连接释放后继续持有
        // 库文件锁，导致用户手动替换/删除 collections.db 与测试清理时报“文件正被另一进程占用”。
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Pooling = false,
        }.ToString();
    }

    /// <summary>收藏库的缺省绝对路径：数据目录下的 collections.db。</summary>
    public static string DefaultDatabasePath =>
        Path.Combine(AppContext.BaseDirectory, AppPaths.DataDirectoryName, DatabaseFileName);

    /// <summary>当前数据库文件的绝对路径（管理界面展示用）。</summary>
    public string DatabasePath => _databasePath;

    public event EventHandler<FavoriteItem>? FavoriteAdded;
    public event EventHandler<string>? FavoriteRemoved;
    public event EventHandler<FavoriteItem>? FavoriteUpdated;

    public Task<bool> AddFavoriteAsync(FavoriteItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (string.IsNullOrWhiteSpace(item.Content))
        {
            return Task.FromResult(false);
        }

        if (!Add(item))
        {
            return Task.FromResult(false);
        }

        FavoriteAdded?.Invoke(this, item);
        return Task.FromResult(true);
    }

    public Task<bool> UpdateFavoriteAsync(FavoriteItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Id <= 0 || string.IsNullOrWhiteSpace(item.Content))
        {
            return Task.FromResult(false);
        }

        // 库文件尚未创建（从未收藏过）时无从更新，直接短路避免无谓建库。
        if (!File.Exists(_databasePath))
        {
            return Task.FromResult(false);
        }

        var content = FavoriteContentNormalizer.Normalize(item.Content);
        if (content.Length == 0)
        {
            return Task.FromResult(false);
        }

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        EnsureSchema(connection);

        // 归一化冲突：新内容命中另一行（Id 不同）时拒绝保存，不静默合并、不覆盖。
        if (NormalizedKeyTakenByOtherRow(connection, content, item.Id))
        {
            return Task.FromResult(false);
        }

        var category = string.IsNullOrWhiteSpace(item.Category) ? "单词" : item.Category.Trim();
        var note = string.IsNullOrWhiteSpace(item.Note) ? null : item.Note;
        var language = string.IsNullOrWhiteSpace(item.Language) ? "日本語" : item.Language.Trim();

        using var update = connection.CreateCommand();
        update.CommandText = """
            UPDATE favorites
            SET category = $category, content = $content, note = $note, language = $language
            WHERE id = $id;
            """;
        update.Parameters.AddWithValue("$category", category);
        update.Parameters.AddWithValue("$content", content);
        update.Parameters.AddWithValue("$note", (object?)note ?? DBNull.Value);
        update.Parameters.AddWithValue("$language", language);
        update.Parameters.AddWithValue("$id", item.Id);
        if (update.ExecuteNonQuery() == 0)
        {
            return Task.FromResult(false);
        }

        // 回填规整字段，让调用方（收藏页编辑态）与库内数据保持一致。
        item.Category = category;
        item.Content = content;
        item.Note = note;
        item.Language = language;

        FavoriteUpdated?.Invoke(this, item);
        return Task.FromResult(true);
    }

    /// <summary>归一化内容是否已被另一行（Id 不同）占用：收藏页冲突提示与服务端兜底共用同一判据。</summary>
    private static bool NormalizedKeyTakenByOtherRow(SqliteConnection connection, string normalizedContent, long id)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, content FROM favorites;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetInt64(0) == id)
            {
                continue;
            }

            if (FavoriteContentNormalizer.Equals(reader.GetString(1), normalizedContent))
            {
                return true;
            }
        }

        return false;
    }

    public Task<bool> RemoveFavoriteAsync(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return Task.FromResult(false);
        }

        // 库文件尚未创建（从未收藏过）时删除必然无命中，直接短路避免无谓建库。
        if (!File.Exists(_databasePath))
        {
            return Task.FromResult(false);
        }

        var removed = Remove(content.Trim());
        if (removed)
        {
            FavoriteRemoved?.Invoke(this, content.Trim());
        }

        return Task.FromResult(removed);
    }

    public bool IsFavorited(string content, string? category = null)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        if (!File.Exists(_databasePath))
        {
            return false;
        }

        // 归一化键：入参携带的注音形态（気付く[きづく]、気付く (きづく)）与库内规范行在此互认。
        var key = FavoriteContentNormalizer.Normalize(content);

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        // 命中判定与内存态一致：忽略首尾空白与大小写；显式传入类别时同时要求类别匹配。
        command.CommandText = string.IsNullOrEmpty(category)
            ? "SELECT COUNT(1) FROM favorites WHERE content = $content COLLATE NOCASE;"
            : "SELECT COUNT(1) FROM favorites WHERE content = $content COLLATE NOCASE AND category = $category COLLATE NOCASE;";
        command.Parameters.AddWithValue("$content", key);
        if (!string.IsNullOrEmpty(category))
        {
            command.Parameters.AddWithValue("$category", category);
        }

        if (command.ExecuteScalar() is long count && count > 0)
        {
            return true;
        }

        // 归一化兜底：历史脏行的形态与规范化入参不同（精确 SQL 查不到），
        // 收藏量小、GetAllFavorites 已是全量快照，全表归一化比对的代价可接受。
        foreach (var item in GetAllFavorites())
        {
            if (!FavoriteContentNormalizer.Equals(item.Content, key))
            {
                continue;
            }

            if (string.IsNullOrEmpty(category) || string.Equals(item.Category, category, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public IReadOnlyList<FavoriteItem> GetAllFavorites()
    {
        if (!File.Exists(_databasePath))
        {
            return Array.Empty<FavoriteItem>();
        }

        var items = new List<FavoriteItem>();
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        // 读路径同样补齐 schema：旧版库文件已存在时先 ALTER 补 language 列再读，
        // 库文件不存在时上方已短路，惰性初始化语义不变。
        EnsureSchema(connection);

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, category, content, note, created_at, language
            FROM favorites
            ORDER BY created_at DESC, id DESC;
            """;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            items.Add(new FavoriteItem
            {
                Id = reader.GetInt64(0),
                Category = reader.GetString(1),
                Content = reader.GetString(2),
                Note = reader.IsDBNull(3) ? null : reader.GetString(3),
                CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)),
                Language = reader.IsDBNull(5) ? "日本語" : reader.GetString(5),
            });
        }

        return items;
    }

    /// <summary>执行写入：同内容（归一化后忽略大小写）upsert，回填主键与规整字段；归一化后为空时拒绝落库。</summary>
    private bool Add(FavoriteItem item)
    {
        // 落库先归一化：模型/划词可能带入注音形态，入库即收敛为规范形态，杜绝新旧两行并存。
        var content = FavoriteContentNormalizer.Normalize(item.Content);
        if (content.Length == 0)
        {
            // 归一化后为空（内容全是注音噪声）视同空内容，拒绝落库。
            item.Content = content;
            return false;
        }

        var category = string.IsNullOrWhiteSpace(item.Category) ? "单词" : item.Category.Trim();
        // 语言由调用方显式赋值（面板探测透传 / 收藏页下拉）；极端留空时回落默认值，
        // 与旧库 ALTER 回填口径一致，保证 NOT NULL 列永不写入空串。
        var language = string.IsNullOrWhiteSpace(item.Language) ? "日本語" : item.Language.Trim();
        var note = string.IsNullOrWhiteSpace(item.Note) ? null : item.Note;
        var createdAtMs = item.CreatedAt.ToUnixTimeMilliseconds();

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        EnsureSchema(connection);

        // 与内存态语义对齐：同一内容（归一化后忽略大小写）重复收藏时更新类别与说明，而不是插入重复行，
        // 否则结果卡星标的“取消收藏”无法稳定移除同源条目。
        // 历史脏行（気付く[きづく]）同样在此被规范化入参命中：更新后自动成为规范形态的单行。
        long existingId;
        using (var find = connection.CreateCommand())
        {
            find.CommandText = "SELECT id, content FROM favorites;";
            using var reader = find.ExecuteReader();
            existingId = 0;
            while (reader.Read())
            {
                if (FavoriteContentNormalizer.Equals(reader.GetString(1), content))
                {
                    existingId = reader.GetInt64(0);
                    break;
                }
            }
        }

        if (existingId > 0)
        {
            using var update = connection.CreateCommand();
            update.CommandText = """
                UPDATE favorites
                SET category = $category, note = $note, created_at = $createdAt, language = $language
                WHERE id = $id;
                """;
            update.Parameters.AddWithValue("$category", category);
            update.Parameters.AddWithValue("$note", (object?)note ?? DBNull.Value);
            update.Parameters.AddWithValue("$createdAt", createdAtMs);
            update.Parameters.AddWithValue("$language", language);
            update.Parameters.AddWithValue("$id", existingId);
            update.ExecuteNonQuery();
        }
        else
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO favorites (category, content, note, created_at, language)
                VALUES ($category, $content, $note, $createdAt, $language);
                SELECT last_insert_rowid();
                """;
            insert.Parameters.AddWithValue("$category", category);
            insert.Parameters.AddWithValue("$content", content);
            insert.Parameters.AddWithValue("$note", (object?)note ?? DBNull.Value);
            insert.Parameters.AddWithValue("$createdAt", createdAtMs);
            insert.Parameters.AddWithValue("$language", language);
            item.Id = insert.ExecuteScalar() is long inserted ? inserted : 0;
        }

        // 回填规整后的字段，让调用方（卡片视图/气泡）与库内数据保持一致。
        item.Id = existingId > 0 ? existingId : item.Id;
        item.Category = category;
        item.Content = content;
        item.Note = note;
        item.Language = language;
        return true;
    }

    /// <summary>执行删除：命中任意归一化形态相同的条目（含历史脏行 気付く[きづく]）。</summary>
    private bool Remove(string content)
    {
        // 先按规范化键做一次精确删除（命中规范行）；未命中再逐行归一化比对清理历史脏行。
        var key = FavoriteContentNormalizer.Normalize(content);

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using (var delete = connection.CreateCommand())
        {
            delete.CommandText = "DELETE FROM favorites WHERE content = $content COLLATE NOCASE;";
            delete.Parameters.AddWithValue("$content", key);
            if (delete.ExecuteNonQuery() > 0)
            {
                return true;
            }
        }

        // 归一化兜底：形态不同但归一化后同一词条的旧行一并点掉，避免“取消收藏”留下影子行。
        List<long> matchedIds;
        using (var find = connection.CreateCommand())
        {
            find.CommandText = "SELECT id, content FROM favorites;";
            using var reader = find.ExecuteReader();
            matchedIds = new List<long>();
            while (reader.Read())
            {
                if (FavoriteContentNormalizer.Equals(reader.GetString(1), key))
                {
                    matchedIds.Add(reader.GetInt64(0));
                }
            }
        }

        if (matchedIds.Count == 0)
        {
            return false;
        }

        using var deleteMatched = connection.CreateCommand();
        deleteMatched.CommandText = $"DELETE FROM favorites WHERE id IN ({string.Join(",", matchedIds)});";
        return deleteMatched.ExecuteNonQuery() > 0;
    }

    /// <summary>保证 favorites 表存在且含 language 列；仅在真正写入（首次收藏）时调用，兑现惰性初始化。</summary>
    private static void EnsureSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        // IF NOT EXISTS 让“无库空态 / 旧版残留库 / 手工删库”都成为合法状态。
        // 新库直接含 language 列（NOT NULL 默认「日本語」）；该默认只对旧库 ALTER 回填的历史行生效。
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS favorites (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                category TEXT NOT NULL,
                content TEXT NOT NULL,
                note TEXT,
                created_at INTEGER NOT NULL,
                language TEXT NOT NULL DEFAULT '日本語'
            );
            """;
        command.ExecuteNonQuery();

        // 旧库补列：仅当 language 列缺失时 ALTER（主人拍板：回填「日本語」只针对已存在的收藏行）。
        var hasLanguage = false;
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA table_info(favorites);";
            using var reader = pragma.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), "language", StringComparison.OrdinalIgnoreCase))
                {
                    hasLanguage = true;
                    break;
                }
            }
        }

        if (hasLanguage)
        {
            return;
        }

        using var alter = connection.CreateCommand();
        alter.CommandText = "ALTER TABLE favorites ADD COLUMN language TEXT NOT NULL DEFAULT '日本語';";
        alter.ExecuteNonQuery();
    }
}
