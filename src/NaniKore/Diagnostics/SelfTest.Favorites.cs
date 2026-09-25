namespace NaniKore.Diagnostics;

using System;
using System.IO;
using NaniKore.Favorites;

internal static partial class SelfTest
{
    public static void RunFavoritesTests()
    {
        Console.WriteLine("--- Running SQLite Favorites Tests ---");

        TestSqliteLazyInitialization();
        TestSqliteAddGetIsFavorited();
        TestSqliteCaseAndWhitespaceMatching();
        TestSqliteCategoryScopedMatching();
        TestSqliteEvents();
        TestSqlitePersistenceAcrossInstances();
        TestSqliteUpsertSemantics();
        TestSqliteRemove();
        TestSqliteRejectsBlankContent();
    }

    /// <summary>创建确定性的临时库路径；调用方负责用完即删。</summary>
    private static string NewTempDatabasePath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nanikore-selftest-{Guid.NewGuid():N}.db");
        // 防御：极端并发下同名文件残留时先清掉，保证断言从空态开始。
        DeleteDatabaseQuietly(path);
        return path;
    }

    /// <summary>尽力而为地清理临时库文件；SQLite 句柄若有短暂延迟释放不使自测失败。</summary>
    private static void DeleteDatabaseQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 临时目录残留文件由操作系统清理，不影响断言语义。
        }
    }

    private static FavoriteItem MakeFavorite(string content, string category = "单词", string? note = null)
    {
        return new FavoriteItem { Category = category, Content = content, Note = note };
    }

    private static void TestSqliteLazyInitialization()
    {
        var path = NewTempDatabasePath();
        try
        {
            var service = new SqliteFavoriteService(path);
            // 惰性初始化：未发生任何收藏动作前，库文件不得被创建。
            Check("SqliteFavorites.Lazy.NoFileAfterConstruct", !File.Exists(path));
            Check("SqliteFavorites.Lazy.GetAllEmpty", service.GetAllFavorites().Count == 0);
            Check("SqliteFavorites.Lazy.IsFavoritedFalse", !service.IsFavorited("通る"));
            Check("SqliteFavorites.Lazy.RemoveFalse", !service.RemoveFavoriteAsync("通る").Result);
            Check("SqliteFavorites.Lazy.NoFileAfterReads", !File.Exists(path));
        }
        finally
        {
            DeleteDatabaseQuietly(path);
        }
    }

    private static void TestSqliteAddGetIsFavorited()
    {
        var path = NewTempDatabasePath();
        try
        {
            var service = new SqliteFavoriteService(path);
            var added = service.AddFavoriteAsync(MakeFavorite("通る", "单词", "读音: とおる\n释义: 通过、走过。")).Result;

            Check("SqliteFavorites.Add.ReturnsTrue", added);
            Check("SqliteFavorites.Add.FileCreated", File.Exists(path));

            var all = service.GetAllFavorites();
            Check("SqliteFavorites.Add.Count", all.Count == 1);
            Check("SqliteFavorites.Add.Content", all[0].Content == "通る");
            Check("SqliteFavorites.Add.Category", all[0].Category == "单词");
            Check("SqliteFavorites.Add.NoteLayered", all[0].Note == "读音: とおる\n释义: 通过、走过。");
            Check("SqliteFavorites.Add.CreatedAtRecent", Math.Abs((DateTimeOffset.Now - all[0].CreatedAt).TotalMinutes) < 5);
            Check("SqliteFavorites.Add.IdAssigned", all[0].Id > 0);
            Check("SqliteFavorites.Add.IsFavorited", service.IsFavorited("通る", "单词"));
        }
        finally
        {
            DeleteDatabaseQuietly(path);
        }
    }

    private static void TestSqliteCaseAndWhitespaceMatching()
    {
        var path = NewTempDatabasePath();
        try
        {
            var service = new SqliteFavoriteService(path);
            service.AddFavoriteAsync(MakeFavorite("Apple", "单词")).Wait();

            // 忽略大小写与首尾空白（与内存态语义一致）。
            Check("SqliteFavorites.Matching.CaseInsensitive", service.IsFavorited("apple"));
            Check("SqliteFavorites.Matching.TrimsInput", service.IsFavorited("  Apple  "));
            Check("SqliteFavorites.Matching.TrimmedStore", service.GetAllFavorites()[0].Content == "Apple");
        }
        finally
        {
            DeleteDatabaseQuietly(path);
        }
    }

    private static void TestSqliteCategoryScopedMatching()
    {
        var path = NewTempDatabasePath();
        try
        {
            var service = new SqliteFavoriteService(path);
            service.AddFavoriteAsync(MakeFavorite("～ない", "语法")).Wait();

            Check("SqliteFavorites.Category.SameCategoryHit", service.IsFavorited("～ない", "语法"));
            Check("SqliteFavorites.Category.OtherCategoryMiss", !service.IsFavorited("～ない", "单词"));
            Check("SqliteFavorites.Category.UnscopedHit", service.IsFavorited("～ない"));
        }
        finally
        {
            DeleteDatabaseQuietly(path);
        }
    }

    private static void TestSqliteEvents()
    {
        var path = NewTempDatabasePath();
        try
        {
            var service = new SqliteFavoriteService(path);
            var addCount = 0;
            var removeCount = 0;
            service.FavoriteAdded += (_, _) => addCount++;
            service.FavoriteRemoved += (_, _) => removeCount++;

            service.AddFavoriteAsync(MakeFavorite("run", "单词", "读音: rʌn")).Wait();
            service.RemoveFavoriteAsync("run").Wait();
            // 空内容不激发事件，也无副作用。
            service.AddFavoriteAsync(MakeFavorite("   ")).Wait();

            Check("SqliteFavorites.Events.AddFired", addCount == 1);
            Check("SqliteFavorites.Events.RemoveFired", removeCount == 1);
            Check("SqliteFavorites.Events.BlankIgnored", service.GetAllFavorites().Count == 0);
        }
        finally
        {
            DeleteDatabaseQuietly(path);
        }
    }

    private static void TestSqlitePersistenceAcrossInstances()
    {
        var path = NewTempDatabasePath();
        try
        {
            var writer = new SqliteFavoriteService(path);
            writer.AddFavoriteAsync(MakeFavorite("学生", "单词", "读音: がくせい")).Wait();
            writer.AddFavoriteAsync(MakeFavorite("～たい", "语法", "释义: 表达愿望")).Wait();

            // 重新打开同一库文件：覆盖升级、进程重启后数据必须仍在。
            var reader = new SqliteFavoriteService(path);
            var all = reader.GetAllFavorites();
            Check("SqliteFavorites.Persist.Count", all.Count == 2);
            Check("SqliteFavorites.Persist.IsFavorited", reader.IsFavorited("学生", "单词"));
            Check("SqliteFavorites.Persist.NoteRoundtrip", all.Any(x => x.Content == "学生" && x.Note == "读音: がくせい"));
        }
        finally
        {
            DeleteDatabaseQuietly(path);
        }
    }

    private static void TestSqliteUpsertSemantics()
    {
        var path = NewTempDatabasePath();
        try
        {
            var service = new SqliteFavoriteService(path);
            service.AddFavoriteAsync(MakeFavorite("通る", "单词", "读音: とおる")).Wait();
            service.AddFavoriteAsync(MakeFavorite(" 通る ", "单词", "读音: とおる\n原形: 通る (动词原形)\n释义: 通过、走过。")).Wait();

            var all = service.GetAllFavorites();
            Check("SqliteFavorites.Upsert.SingleRow", all.Count == 1);
            Check("SqliteFavorites.Upsert.NoteUpdated", all[0].Note!.Contains("动词原形"));
        }
        finally
        {
            DeleteDatabaseQuietly(path);
        }
    }

    private static void TestSqliteRemove()
    {
        var path = NewTempDatabasePath();
        try
        {
            var service = new SqliteFavoriteService(path);
            service.AddFavoriteAsync(MakeFavorite(" eat ", "短语")).Wait();

            var removed = service.RemoveFavoriteAsync("eat").Result;
            Check("SqliteFavorites.Remove.TrimmedHit", removed);
            Check("SqliteFavorites.Remove.EmptyAfter", service.GetAllFavorites().Count == 0);
            Check("SqliteFavorites.Remove.IsFavoritedFalse", !service.IsFavorited(" eat "));
            Check("SqliteFavorites.Remove.SecondRemoveFalse", !service.RemoveFavoriteAsync("eat").Result);
        }
        finally
        {
            DeleteDatabaseQuietly(path);
        }
    }

    private static void TestSqliteRejectsBlankContent()
    {
        var path = NewTempDatabasePath();
        try
        {
            var service = new SqliteFavoriteService(path);
            var added = service.AddFavoriteAsync(MakeFavorite("")).Result;

            Check("SqliteFavorites.Blank.Rejected", !added);
            Check("SqliteFavorites.Blank.NoFile", !File.Exists(path));
        }
        finally
        {
            DeleteDatabaseQuietly(path);
        }
    }
}
