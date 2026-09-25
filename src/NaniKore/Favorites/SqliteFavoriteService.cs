namespace NaniKore.Favorites;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

/// <summary>
/// 基于 SQLite 的持久化收藏服务（切片 5）。
/// 数据库文件固定落在应用当前根目录的 <c>nanikore.db</c>，绿色便携、覆盖升级不丢数据；
/// 采用惰性初始化：未发生任何收藏前不创建库文件，删除库文件后按空态继续工作。
/// 与 <see cref="InMemoryFavoriteService"/> 保持完全一致的操作语义（同内容大小写不敏感 upsert），
/// 保证切片 4 的星标同步逻辑无需任何改动即可切换到持久化实现。
/// SQLite 本地读写为毫秒级同步操作，接口的 Task 返回以已完成任务透出，避免无谓的线程切换。
/// </summary>
public sealed class SqliteFavoriteService : IFavoriteService
{
    /// <summary>数据库文件名：与 exe 同目录，随应用整体拷贝/备份。</summary>
    public const string DatabaseFileName = "nanikore.db";

    private readonly string _databasePath;
    private readonly string _connectionString;

    public SqliteFavoriteService(string? databasePath = null)
    {
        // 默认路径取应用当前根目录；测试可注入临时路径以获得确定性隔离。
        _databasePath = databasePath ?? Path.Combine(AppContext.BaseDirectory, DatabaseFileName);
        // 显式关闭连接池：桌面单用户场景没有复用收益，而池化句柄会在连接释放后继续持有
        // 库文件锁，导致用户手动替换/删除 nanikore.db 与测试清理时报“文件正被另一进程占用”。
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Pooling = false,
        }.ToString();
    }

    /// <summary>当前数据库文件的绝对路径（管理界面展示用）。</summary>
    public string DatabasePath => _databasePath;

    public event EventHandler<FavoriteItem>? FavoriteAdded;
    public event EventHandler<string>? FavoriteRemoved;

    public Task<bool> AddFavoriteAsync(FavoriteItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (string.IsNullOrWhiteSpace(item.Content))
        {
            return Task.FromResult(false);
        }

        Add(item);
        FavoriteAdded?.Invoke(this, item);
        return Task.FromResult(true);
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

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        // 命中判定与内存态一致：忽略首尾空白与大小写；显式传入类别时同时要求类别匹配。
        command.CommandText = string.IsNullOrEmpty(category)
            ? "SELECT COUNT(1) FROM favorites WHERE content = $content COLLATE NOCASE;"
            : "SELECT COUNT(1) FROM favorites WHERE content = $content COLLATE NOCASE AND category = $category COLLATE NOCASE;";
        command.Parameters.AddWithValue("$content", content.Trim());
        if (!string.IsNullOrEmpty(category))
        {
            command.Parameters.AddWithValue("$category", category);
        }

        return command.ExecuteScalar() is long count && count > 0;
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

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, category, content, note, created_at
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
            });
        }

        return items;
    }

    /// <summary>执行写入：同内容（忽略大小写）upsert，回填主键与规整字段。</summary>
    private void Add(FavoriteItem item)
    {
        var content = item.Content.Trim();
        var category = string.IsNullOrWhiteSpace(item.Category) ? "单词" : item.Category.Trim();
        var note = string.IsNullOrWhiteSpace(item.Note) ? null : item.Note;
        var createdAtMs = item.CreatedAt.ToUnixTimeMilliseconds();

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        EnsureSchema(connection);

        // 与内存态语义对齐：同一内容（忽略大小写）重复收藏时更新类别与说明，而不是插入重复行，
        // 否则结果卡星标的“取消收藏”无法稳定移除同源条目。
        long existingId;
        using (var find = connection.CreateCommand())
        {
            find.CommandText = "SELECT id FROM favorites WHERE content = $content COLLATE NOCASE LIMIT 1;";
            find.Parameters.AddWithValue("$content", content);
            existingId = find.ExecuteScalar() is long id ? id : 0;
        }

        if (existingId > 0)
        {
            using var update = connection.CreateCommand();
            update.CommandText = """
                UPDATE favorites
                SET category = $category, note = $note, created_at = $createdAt
                WHERE id = $id;
                """;
            update.Parameters.AddWithValue("$category", category);
            update.Parameters.AddWithValue("$note", (object?)note ?? DBNull.Value);
            update.Parameters.AddWithValue("$createdAt", createdAtMs);
            update.Parameters.AddWithValue("$id", existingId);
            update.ExecuteNonQuery();
        }
        else
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO favorites (category, content, note, created_at)
                VALUES ($category, $content, $note, $createdAt);
                SELECT last_insert_rowid();
                """;
            insert.Parameters.AddWithValue("$category", category);
            insert.Parameters.AddWithValue("$content", content);
            insert.Parameters.AddWithValue("$note", (object?)note ?? DBNull.Value);
            insert.Parameters.AddWithValue("$createdAt", createdAtMs);
            item.Id = insert.ExecuteScalar() is long inserted ? inserted : 0;
        }

        // 回填规整后的字段，让调用方（卡片视图/气泡）与库内数据保持一致。
        item.Id = existingId > 0 ? existingId : item.Id;
        item.Category = category;
        item.Content = content;
        item.Note = note;
    }

    /// <summary>执行删除：命中任意大小写形式的同内容条目。</summary>
    private bool Remove(string content)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var delete = connection.CreateCommand();
        delete.CommandText = "DELETE FROM favorites WHERE content = $content COLLATE NOCASE;";
        delete.Parameters.AddWithValue("$content", content);
        return delete.ExecuteNonQuery() > 0;
    }

    /// <summary>保证 favorites 表存在；仅在真正写入（首次收藏）时调用，兑现惰性初始化。</summary>
    private static void EnsureSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        // IF NOT EXISTS 让“无库空态 / 旧版残留库 / 手工删库”都成为合法状态。
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS favorites (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                category TEXT NOT NULL,
                content TEXT NOT NULL,
                note TEXT,
                created_at INTEGER NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }
}
