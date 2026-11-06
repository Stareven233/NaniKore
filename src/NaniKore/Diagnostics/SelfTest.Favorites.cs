namespace NaniKore.Diagnostics;

using System;
using System.IO;
using Microsoft.Data.Sqlite;
using NaniKore.Favorites;

internal static partial class SelfTest
{
    private static void RunFavoritesTests()
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
        TestSqliteNormalizedMatchingAndStorage();
        TestInMemoryNormalizedMatching();
        TestSqliteLegacyLanguageColumnMigration();
        TestSqliteUpdateFavorite();
        TestInMemoryUpdateFavorite();
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

    /// <summary>以临时库路径执行用例：建库前先清残留、用完即删，省去每个用例重复 try/finally。</summary>
    private static void WithTempDatabasePath(Action<string> body)
    {
        var path = NewTempDatabasePath();
        try
        {
            body(path);
        }
        finally
        {
            DeleteDatabaseQuietly(path);
        }
    }

    private static FavoriteItem MakeFavorite(string content, string category = "单词", string? note = null)
    {
        return new FavoriteItem { Category = category, Content = content, Note = note };
    }

    private static void TestSqliteLazyInitialization()
    {
        WithTempDatabasePath(path =>
        {
            var service = new SqliteFavoriteService(path);
            // 惰性初始化：未发生任何收藏动作前，库文件不得被创建。
            Check("SqliteFavorites.Lazy.NoFileAfterConstruct", !File.Exists(path));
            Check("SqliteFavorites.Lazy.GetAllEmpty", service.GetAllFavorites().Count == 0);
            Check("SqliteFavorites.Lazy.IsFavoritedFalse", !service.IsFavorited("通る"));
            Check("SqliteFavorites.Lazy.RemoveFalse", !service.RemoveFavoriteAsync("通る").Result);
            Check("SqliteFavorites.Lazy.NoFileAfterReads", !File.Exists(path));
        });
    }

    private static void TestSqliteAddGetIsFavorited()
    {
        WithTempDatabasePath(path =>
        {
            var service = new SqliteFavoriteService(path);
            var added = service.AddFavoriteAsync(MakeFavorite("通る", "单词", "とおる\n通过、走过。")).Result;

            Check("SqliteFavorites.Add.ReturnsTrue", added);
            Check("SqliteFavorites.Add.FileCreated", File.Exists(path));

            var all = service.GetAllFavorites();
            Check("SqliteFavorites.Add.Count", all.Count == 1);
            Check("SqliteFavorites.Add.Content", all[0].Content == "通る");
            Check("SqliteFavorites.Add.Category", all[0].Category == "单词");
            Check("SqliteFavorites.Add.NoteLayered", all[0].Note == "とおる\n通过、走过。");
            Check("SqliteFavorites.Add.CreatedAtRecent", Math.Abs((DateTimeOffset.Now - all[0].CreatedAt).TotalMinutes) < 5);
            Check("SqliteFavorites.Add.IdAssigned", all[0].Id > 0);
            Check("SqliteFavorites.Add.IsFavorited", service.IsFavorited("通る", "单词"));
        });
    }

    private static void TestSqliteCaseAndWhitespaceMatching()
    {
        WithTempDatabasePath(path =>
        {
            var service = new SqliteFavoriteService(path);
            service.AddFavoriteAsync(MakeFavorite("Apple", "单词")).Wait();

            // 忽略大小写与首尾空白（与内存态语义一致）。
            Check("SqliteFavorites.Matching.CaseInsensitive", service.IsFavorited("apple"));
            Check("SqliteFavorites.Matching.TrimsInput", service.IsFavorited("  Apple  "));
            Check("SqliteFavorites.Matching.TrimmedStore", service.GetAllFavorites()[0].Content == "Apple");
        });
    }

    private static void TestSqliteCategoryScopedMatching()
    {
        WithTempDatabasePath(path =>
        {
            var service = new SqliteFavoriteService(path);
            service.AddFavoriteAsync(MakeFavorite("～ない", "语法")).Wait();

            Check("SqliteFavorites.Category.SameCategoryHit", service.IsFavorited("～ない", "语法"));
            Check("SqliteFavorites.Category.OtherCategoryMiss", !service.IsFavorited("～ない", "单词"));
            Check("SqliteFavorites.Category.UnscopedHit", service.IsFavorited("～ない"));
        });
    }

    private static void TestSqliteEvents()
    {
        WithTempDatabasePath(path =>
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
        });
    }

    private static void TestSqlitePersistenceAcrossInstances()
    {
        WithTempDatabasePath(path =>
        {
            var writer = new SqliteFavoriteService(path);
            writer.AddFavoriteAsync(MakeFavorite("学生", "单词", "がくせい")).Wait();
            writer.AddFavoriteAsync(MakeFavorite("～たい", "语法", "表达愿望")).Wait();

            // 重新打开同一库文件：覆盖升级、进程重启后数据必须仍在。
            var reader = new SqliteFavoriteService(path);
            var all = reader.GetAllFavorites();
            Check("SqliteFavorites.Persist.Count", all.Count == 2);
            Check("SqliteFavorites.Persist.IsFavorited", reader.IsFavorited("学生", "单词"));
            Check("SqliteFavorites.Persist.NoteRoundtrip", all.Any(x => x.Content == "学生" && x.Note == "がくせい"));
        });
    }

    private static void TestSqliteUpsertSemantics()
    {
        WithTempDatabasePath(path =>
        {
            var service = new SqliteFavoriteService(path);
            service.AddFavoriteAsync(MakeFavorite("通る", "单词", "とおる")).Wait();
            service.AddFavoriteAsync(MakeFavorite(" 通る ", "单词", "とおる\n通る (动词原形)\n通过、走过。")).Wait();

            var all = service.GetAllFavorites();
            Check("SqliteFavorites.Upsert.SingleRow", all.Count == 1);
            Check("SqliteFavorites.Upsert.NoteUpdated", all[0].Note!.Contains("动词原形"));
        });
    }

    private static void TestSqliteRemove()
    {
        WithTempDatabasePath(path =>
        {
            var service = new SqliteFavoriteService(path);
            service.AddFavoriteAsync(MakeFavorite(" eat ", "短语")).Wait();

            var removed = service.RemoveFavoriteAsync("eat").Result;
            Check("SqliteFavorites.Remove.TrimmedHit", removed);
            Check("SqliteFavorites.Remove.EmptyAfter", service.GetAllFavorites().Count == 0);
            Check("SqliteFavorites.Remove.IsFavoritedFalse", !service.IsFavorited(" eat "));
            Check("SqliteFavorites.Remove.SecondRemoveFalse", !service.RemoveFavoriteAsync("eat").Result);
        });
    }

    private static void TestSqliteRejectsBlankContent()
    {
        WithTempDatabasePath(path =>
        {
            var service = new SqliteFavoriteService(path);
            var added = service.AddFavoriteAsync(MakeFavorite("")).Result;

            Check("SqliteFavorites.Blank.Rejected", !added);
            Check("SqliteFavorites.Blank.NoFile", !File.Exists(path));
        });
    }

    private static void TestSqliteNormalizedMatchingAndStorage()
    {
        WithTempDatabasePath(path =>
        {
            var service = new SqliteFavoriteService(path);

            // 入库归一化：带注音形态的写入落库即收敛为规范形态。
            service.AddFavoriteAsync(MakeFavorite("気付く[きづく]", "单词", "读音: きづく")).Wait();
            Check("SqliteFavorites.Normalize.StoredStripped", service.GetAllFavorites()[0].Content == "気付く");

            // 三种括号形态互认：词条本体、方括号注音、圆括号读音。
            Check("SqliteFavorites.Normalize.SquareBracketHit", service.IsFavorited("気付く[きづく]"));
            Check("SqliteFavorites.Normalize.RoundBracketHit", service.IsFavorited("気付く (きづく)"));
            Check("SqliteFavorites.Normalize.PlainHit", service.IsFavorited("気付く"));

            // 归一化 upsert：旧脏行与规范形态重复收藏时更新同一行，不产生第二行。
            service.AddFavoriteAsync(MakeFavorite("気付く (きづく)", "单词", "读音: きづく\n释义: 注意到")).Wait();
            Check("SqliteFavorites.Normalize.UpsertSingleRow", service.GetAllFavorites().Count == 1);
            Check("SqliteFavorites.Normalize.UpsertNoteUpdated", service.GetAllFavorites()[0].Note!.Contains("注意到"));

            // 移除按归一化匹配：用另一形态入参也能点掉规范行。
            Check("SqliteFavorites.Normalize.RemoveByOtherForm", service.RemoveFavoriteAsync("気付く[きづく]").Result);
            Check("SqliteFavorites.Normalize.RemoveEmpties", service.GetAllFavorites().Count == 0);

            // 类别在归一化匹配下仍然生效：仅作默认写入值，不影响跨类别去重语义。
            service.AddFavoriteAsync(MakeFavorite("～ない", "语法")).Wait();
            Check("SqliteFavorites.Normalize.CategoryScopeStillWorks",
                service.IsFavorited("～ない", "语法") && !service.IsFavorited("～ない", "单词"));
            Check("SqliteFavorites.Normalize.CategoryScopeNormalizedHit", service.IsFavorited("～ない[ない]", "语法"));
        });

        // 归一化函数直断：IPA、代码下标、行首标签、全角括号汉字一律不得误剥。
        Check("FavoriteNormalizer.IpaUntouched", FavoriteContentNormalizer.Normalize("ubiquitous[juːˈbɪkwɪtəs]") == "ubiquitous[juːˈbɪkwɪtəs]");
        Check("FavoriteNormalizer.CodeIndexUntouched", FavoriteContentNormalizer.Normalize("arr[0]") == "arr[0]");
        Check("FavoriteNormalizer.LeadingTagUntouched", FavoriteContentNormalizer.Normalize("[标签]词条") == "[标签]词条");
        Check("FavoriteNormalizer.KanjiParenUntouched", FavoriteContentNormalizer.Normalize("〜わけだ（理由）") == "〜わけだ（理由）");
        Check("FavoriteNormalizer.StripSquare", FavoriteContentNormalizer.Normalize("気付く[きづく]") == "気付く");
        Check("FavoriteNormalizer.StripRound", FavoriteContentNormalizer.Normalize("気付く (きづく)") == "気付く");
        Check("FavoriteNormalizer.StripFullWidthRound", FavoriteContentNormalizer.Normalize("気付く（きづく）") == "気付く");
        Check("FavoriteNormalizer.TrimsInput", FavoriteContentNormalizer.Normalize("  Apple  ") == "Apple");
        Check("FavoriteNormalizer.BlankToEmpty", FavoriteContentNormalizer.Normalize("   ") == string.Empty);
        Check("FavoriteNormalizer.EqualsCrossForms", FavoriteContentNormalizer.Equals("気付く[きづく]", "気付く"));
    }

    private static void TestInMemoryNormalizedMatching()
    {
        // 内存态与 SQLite 态共用同一归一化键，两处实现不得漂移。
        var service = new InMemoryFavoriteService();
        service.AddFavoriteAsync(MakeFavorite("気付く[きづく]", "单词")).Wait();

        Check("InMemoryFavorites.Normalize.StoredStripped", service.GetAllFavorites()[0].Content == "気付く");
        Check("InMemoryFavorites.Normalize.CrossFormHit", service.IsFavorited("気付く (きづく)"));
        Check("InMemoryFavorites.Normalize.RemoveByOtherForm", service.RemoveFavoriteAsync("気付く[きづく]").Result);
        Check("InMemoryFavorites.Normalize.RemoveEmpties", service.GetAllFavorites().Count == 0);
    }

    private static void TestInMemoryUpdateFavorite()
    {
        var service = new InMemoryFavoriteService();
        service.AddFavoriteAsync(MakeFavorite("通る", "单词")).Wait();
        service.AddFavoriteAsync(MakeFavorite("～ない", "语法")).Wait();

        var updatedCount = 0;
        service.FavoriteUpdated += (_, _) => updatedCount++;

        var target = service.GetAllFavorites().First(x => x.Content == "通る");
        target.Content = "通[とお]る";
        target.Note = "读音: とおる";
        Check("InMemoryFavorites.Update.Applied", service.UpdateFavoriteAsync(target).Result);
        Check("InMemoryFavorites.Update.StoredNormalized", service.GetAllFavorites().Single(x => x.Id == target.Id).Content == "通る");
        Check("InMemoryFavorites.Update.EventFired", updatedCount == 1);

        // 归一化冲突：改后内容命中另一行 → 拒绝。注意构造全新条目（内存态快照即库内对象本身）。
        var other = service.GetAllFavorites().First(x => x.Content == "～ない");
        var edited = new FavoriteItem { Id = other.Id, Category = other.Category, Content = "通る (とおる)" };
        Check("InMemoryFavorites.Update.ConflictRejected", !service.UpdateFavoriteAsync(edited).Result);
        Check("InMemoryFavorites.Update.ConflictKeepsOriginal", service.GetAllFavorites().Any(x => x.Content == "～ない"));
    }

    private static void TestSqliteLegacyLanguageColumnMigration()
    {
        WithTempDatabasePath(path =>
        {
            // 手工构造旧 schema（无 language 列）的库并塞一行历史收藏，模拟主人现网旧库。
            using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                connection.Open();
                using var create = connection.CreateCommand();
                create.CommandText = """
                    CREATE TABLE favorites (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        category TEXT NOT NULL,
                        content TEXT NOT NULL,
                        note TEXT,
                        created_at INTEGER NOT NULL
                    );
                    """;
                create.ExecuteNonQuery();
                using var insert = connection.CreateCommand();
                insert.CommandText =
                    "INSERT INTO favorites (category, content, note, created_at) VALUES ('单词', '通る', '读音: とおる', 1730000000000);";
                insert.ExecuteNonQuery();
            }

            var service = new SqliteFavoriteService(path);
            var all = service.GetAllFavorites();
            Check("SqliteFavorites.Migrate.ColumnAddedOnRead", all.Count == 1);
            // 主人拍板：旧行回填「日本語」只针对已存在的收藏行。
            Check("SqliteFavorites.Migrate.LegacyRowBackfilledJapanese", all[0].Language == "日本語");

            // 新收藏一律显式赋值语言，不依赖列默认。
            var newItem = MakeFavorite("student", "单词");
            newItem.Language = "English";
            service.AddFavoriteAsync(newItem).Wait();
            Check(
                "SqliteFavorites.Migrate.NewRowExplicitLanguage",
                service.GetAllFavorites().Any(x => x.Content == "student" && x.Language == "English"));

            // 迁移幂等：重新打开不再重复 ALTER，数据不丢。
            var reopened = new SqliteFavoriteService(path);
            Check("SqliteFavorites.Migrate.IdempotentReopen", reopened.GetAllFavorites().Count == 2);
        });
    }

    private static void TestSqliteUpdateFavorite()
    {
        WithTempDatabasePath(path =>
        {
            var service = new SqliteFavoriteService(path);
            service.AddFavoriteAsync(MakeFavorite("通る", "单词", "读音: とおる")).Wait();
            service.AddFavoriteAsync(MakeFavorite("～ない", "语法")).Wait();

            var updatedCount = 0;
            service.FavoriteUpdated += (_, _) => updatedCount++;

            // 按 Id 更新多字段；content 仍走归一化（编辑后带回注音形态也收敛）。
            var target = service.GetAllFavorites().First(x => x.Content == "通る");
            target.Category = "短语";
            target.Content = "通[とお]る";
            target.Note = "释义: 修订";
            Check("SqliteFavorites.Update.Applied", service.UpdateFavoriteAsync(target).Result);
            Check("SqliteFavorites.Update.EventFired", updatedCount == 1);

            var after = service.GetAllFavorites().Single(x => x.Id == target.Id);
            Check("SqliteFavorites.Update.FieldsPersisted",
                after.Category == "短语" && after.Content == "通る" && after.Note == "释义: 修订");
            Check("SqliteFavorites.Update.CountUnchanged", service.GetAllFavorites().Count == 2);

            // 归一化冲突：改后内容与另一行（注音形态不同）同一词条 → 拒绝且原行不动。
            var conflict = service.GetAllFavorites().First(x => x.Id == target.Id);
            conflict.Content = "～ない[ない]";
            Check("SqliteFavorites.Update.ConflictRejected", !service.UpdateFavoriteAsync(conflict).Result);
            Check("SqliteFavorites.Update.ConflictKeepsOriginal",
                service.GetAllFavorites().Single(x => x.Id == target.Id).Content == "通る");

            // 空内容与未知 Id 一律拒绝。
            conflict.Content = "   ";
            Check("SqliteFavorites.Update.BlankRejected", !service.UpdateFavoriteAsync(conflict).Result);
            Check("SqliteFavorites.Update.UnknownIdRejected",
                !service.UpdateFavoriteAsync(new FavoriteItem { Id = 99999, Content = "x" }).Result);
        });
    }
}
