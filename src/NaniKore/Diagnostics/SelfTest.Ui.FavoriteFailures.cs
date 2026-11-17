namespace NaniKore.Diagnostics;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using NaniKore.App;
using NaniKore.App.Controls;
using NaniKore.App.Settings;
using NaniKore.Config;
using NaniKore.Favorites;

/// <summary>
/// 无 UI 自测（收藏库故障隔离）：只读文件、独占句柄与损坏库三种真实故障下，
/// 卡片星标、划词气泡与收藏管理页都要给出可理解提示、保持真实落库状态且不抛异常。
/// 故障全部构造在临时目录，不触碰真实收藏库。
/// </summary>
internal static partial class SelfTest
{
    private const string CorruptDatabaseContent =
        "这不是 SQLite 数据库：文件头被破坏后 sqlite 会报 file is not a database";

    private static void RunFavoriteFailureUiTests()
    {
        EnsureThemedApplication();
        TestFavoriteFailureMessages();
        TestCardFavoriteFailureIsolation();
        TestSelectionBubbleFavoriteFailureIsolation();
        TestFavoritesPageReadFailureRecovery();
        TestFavoritesPageWriteFailureRecovery();
    }

    /// <summary>异常描述必须给出可理解原因与恢复建议，而不是把英文 SQLite 消息原样抛给用户。</summary>
    private static void TestFavoriteFailureMessages()
    {
        using var database = new TempFavoriteDatabase("messages-corrupt");
        File.WriteAllText(database.Path, CorruptDatabaseContent);
        var corruptMessage = CaptureFavoriteFailure("收藏", () =>
        {
            _ = new SqliteFavoriteService(database.Path)
                .AddFavoriteAsync(new FavoriteItem { Content = "通る" })
                .GetAwaiter().GetResult();
        });
        Check(
            "corrupt favorite database is described with a recovery hint",
            corruptMessage.Contains("收藏失败：") && corruptMessage.Contains("损坏") &&
            corruptMessage.Contains(".data/collections.db"),
            corruptMessage);

        using var readonlyDatabase = new TempFavoriteDatabase("messages-readonly");
        var readonlyService = new SqliteFavoriteService(readonlyDatabase.Path);
        readonlyService.AddFavoriteAsync(new FavoriteItem { Content = "走る" }).GetAwaiter().GetResult();
        File.SetAttributes(readonlyDatabase.Path, FileAttributes.ReadOnly);
        var readonlyMessage = CaptureFavoriteFailure("删除收藏", () =>
        {
            _ = readonlyService.RemoveFavoriteAsync("走る").GetAwaiter().GetResult();
        });
        Check(
            "read-only favorite database is described as read-only",
            readonlyMessage.Contains("删除收藏失败：") && readonlyMessage.Contains("只读"),
            readonlyMessage);
        File.SetAttributes(readonlyDatabase.Path, FileAttributes.Normal);

        using var lockedDatabase = new TempFavoriteDatabase("messages-locked");
        var lockedService = new SqliteFavoriteService(lockedDatabase.Path);
        lockedService.AddFavoriteAsync(new FavoriteItem { Content = "食べる" }).GetAwaiter().GetResult();
        string lockedMessage;
        using (new FileStream(lockedDatabase.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            lockedMessage = CaptureFavoriteFailure("读取收藏列表", () => lockedService.GetAllFavorites());
        }

        Check(
            "exclusively locked favorite database is described as unopenable",
            lockedMessage.Contains("读取收藏列表失败：") && lockedMessage.Contains("无法打开"),
            lockedMessage);
    }

    /// <summary>精讲卡片：库不可读时星标降级为未收藏，落库失败时星标保持原状且不通知收藏成功。</summary>
    private static void TestCardFavoriteFailureIsolation()
    {
        using var database = new TempFavoriteDatabase("card");
        var service = new SqliteFavoriteService(database.Path);
        service.AddFavoriteAsync(new FavoriteItem { Category = "单词", Content = "走る", Language = "日本語" })
            .GetAwaiter().GetResult();

        var view = new ExplanationCardView();
        var failures = new List<string>();
        var toggles = new List<bool>();
        view.FavoriteFailed += (_, message) => failures.Add(message);
        view.FavoriteToggled += (_, args) => toggles.Add(args.Added);

        view.SetFavoriteService(service);
        view.SetText("""
            ### [词汇/短语]
            - **走る** | はしる | 动词 | 奔跑
            - **食べる** | たべる | 动词 | 吃
            """);
        Check(
            "card view reads real favorite state before the database breaks",
            view.VocabularyItems.Count == 2 &&
            view.VocabularyItems[0].FavoriteIcon == "★" &&
            view.VocabularyItems[1].FavoriteIcon == "☆");

        // 破坏库后写入：既不能抛异常，也不能假装成功
        File.WriteAllText(database.Path, CorruptDatabaseContent);

        view.ToggleVocabularyFavoriteAsync(view.VocabularyItems[1]).GetAwaiter().GetResult();
        Check(
            "card keeps an unfavorited star when persisting fails",
            view.VocabularyItems[1].FavoriteIcon == "☆" && toggles.Count == 0,
            string.Join(" | ", failures));
        Check(
            "card reports the failed favorite with a recovery hint",
            failures.Count == 1 && failures[0].Contains("收藏失败：") && failures[0].Contains("损坏"),
            string.Join(" | ", failures));

        failures.Clear();
        view.ToggleVocabularyFavoriteAsync(view.VocabularyItems[0]).GetAwaiter().GetResult();
        Check(
            "card keeps a favorited star when removing fails",
            view.VocabularyItems[0].FavoriteIcon == "★" && toggles.Count == 0);
        Check(
            "card reports the failed removal instead of announcing success",
            failures.Count == 1 && failures[0].Contains("取消收藏失败："),
            string.Join(" | ", failures));

        // 读取路径：损坏库上的重新渲染按未收藏降级，不抛异常
        failures.Clear();
        view.SetText("""
            ### [词汇/短语]
            - **走る** | はしる | 动词 | 奔跑
            """);
        Check(
            "card renders degraded stars instead of throwing on unreadable database",
            view.VocabularyItems.Count == 1 && view.VocabularyItems[0].FavoriteIcon == "☆" && failures.Count == 0);
    }

    /// <summary>划词气泡：损坏库下打开气泡与收藏都不能抛异常，失败时星标不变并上报提示。</summary>
    private static void TestSelectionBubbleFavoriteFailureIsolation()
    {
        using var database = new TempFavoriteDatabase("bubble");
        File.WriteAllText(database.Path, CorruptDatabaseContent);
        var service = new SqliteFavoriteService(database.Path);

        var panel = new ResultPanelWindow();
        try
        {
            // 注入与打开气泡都会读库：损坏库不得从 UI 事件里抛出去
            panel.SetFavoriteService(service);
            var bubble = Find<SelectionBubblePopup>(panel, "SelectionBubble");
            Check("result panel mounts the selection bubble", bubble is not null);
            if (bubble is null)
            {
                return;
            }

            var failures = new List<string>();
            var requested = 0;
            bubble.FavoriteFailed += (_, message) => failures.Add(message);
            bubble.FavoriteRequested += (_, _) => requested++;

            bubble.ShowAt(new Point(100, 100), "走る");
            Check(
                "selection bubble opens with an unfavorited star on an unreadable database",
                bubble.PopupHost.IsOpen && bubble.FavoriteBtn is not null);

            bubble.ToggleFavoriteAsync().GetAwaiter().GetResult();
            Check(
                "selection bubble keeps the star unchanged when favoriting fails",
                failures.Count == 1 && requested == 0 && failures[0].Contains("收藏失败："),
                string.Join(" | ", failures));

            var status = Find<TextBlock>(panel, "StatusTextBlock");
            var statusText = status?.Text ?? string.Empty;
            Check(
                "result panel surfaces the favorite failure to the user",
                statusText.Contains("收藏失败：") && status?.Classes.Contains("status-error") == true,
                statusText.Length == 0 ? "(missing)" : statusText);
        }
        finally
        {
            panel.Close();
        }
    }

    /// <summary>收藏管理页：列表读取失败保留上一次快照并提示，重置损坏库后刷新即可恢复。</summary>
    private static void TestFavoritesPageReadFailureRecovery()
    {
        using var database = new TempFavoriteDatabase("page-read");
        var service = new SqliteFavoriteService(database.Path);
        service.AddFavoriteAsync(new FavoriteItem { Category = "单词", Content = "走る" }).GetAwaiter().GetResult();

        var page = new FavoritesPage(service);
        page.Load(new AppConfig());
        Check("favorites page loads the working list first", FavoriteEntryCount(page) == 1);

        File.WriteAllText(database.Path, CorruptDatabaseContent);
        RaiseClick(page.RefreshButton);
        Check(
            "favorites page keeps the last good list when reading fails",
            FavoriteEntryCount(page) == 1);
        Check(
            "favorites page reports read failure instead of an empty list",
            page.Status.IsOpen && page.Status.Severity == InfoSeverity.Error &&
            page.Status.Message.Contains("读取收藏列表失败：") &&
            page.Status.Message.Contains("损坏"),
            page.Status.Message);

        // 恢复路径：按提示备份后重置库文件，再刷新即可回到可用状态
        File.Delete(database.Path);
        RaiseClick(page.RefreshButton);
        Check(
            "favorites page recovers after the damaged database is reset",
            FavoriteEntryCount(page) == 0 && page.Status.Severity == InfoSeverity.Neutral &&
            page.Status.Message.Contains("已刷新收藏列表"),
            page.Status.Message);
        Check(
            "favorites page shows the empty hint after a successful refresh",
            page.EmptyHint.IsVisible);
    }

    /// <summary>收藏管理页：只读库下删除与编辑失败都要提示且不留虚假结果，去掉只读属性后可直接重试。</summary>
    private static void TestFavoritesPageWriteFailureRecovery()
    {
        using var database = new TempFavoriteDatabase("page-write");
        var service = new SqliteFavoriteService(database.Path);
        service.AddFavoriteAsync(new FavoriteItem { Category = "单词", Content = "走る", Note = "はしる" })
            .GetAwaiter().GetResult();

        var page = new FavoritesPage(service);
        page.Load(new AppConfig());
        var entry = FavoriteEntry(page);
        Check("favorites page realizes the entry for delete and edit", entry is not null);

        File.SetAttributes(database.Path, FileAttributes.ReadOnly);
        try
        {
            if (entry is not null)
            {
                page.DeleteFavoriteAsync(entry).GetAwaiter().GetResult();
                Check(
                    "favorites page keeps the row when deleting fails",
                    FavoriteEntryCount(page) == 1);
                Check(
                    "favorites page reports delete failure with a recovery hint",
                    page.Status.Severity == InfoSeverity.Error &&
                    page.Status.Message.Contains("删除收藏失败：") &&
                    page.Status.Message.Contains("只读"),
                    page.Status.Message);

                var target = service.GetAllFavorites().Single();
                var saved = page.SaveEditAsync(target, "通る", "とおる", "单词", "日本語").GetAwaiter().GetResult();
                Check("favorites page refuses a save that cannot be persisted", !saved);
                Check(
                    "favorites page reports edit failure without faking a saved row",
                    page.Status.Severity == InfoSeverity.Error &&
                    page.Status.Message.Contains("保存收藏失败："),
                    page.Status.Message);
                Check(
                    "favorites page rolls the in-memory snapshot back after a failed save",
                    target.Content == "走る" && target.Note == "はしる");
            }
        }
        finally
        {
            File.SetAttributes(database.Path, FileAttributes.Normal);
        }

        // 恢复条件后重试同一入口：无需重开页面
        var retryEntry = FavoriteEntry(page);
        if (retryEntry is not null)
        {
            page.DeleteFavoriteAsync(retryEntry).GetAwaiter().GetResult();
        }

        Check(
            "favorites page deletes the same row after the read-only attribute is cleared",
            FavoriteEntryCount(page) == 0 && page.Status.Severity == InfoSeverity.Success,
            page.Status.Message);
    }

    private static int FavoriteEntryCount(FavoritesPage page) =>
        page.FavoriteList.ItemsSource?.Cast<object>().Count() ?? 0;

    private static FavoriteDisplayViewModel? FavoriteEntry(FavoritesPage page) =>
        page.FavoriteList.ItemsSource?.Cast<object>().OfType<FavoriteDisplayViewModel>().FirstOrDefault();

    /// <summary>执行一次必失败的操作并取回面向用户的描述；操作未失败时返回可见的失败信号。</summary>
    private static string CaptureFavoriteFailure(string action, Action operation)
    {
        try
        {
            operation();
        }
        catch (Exception exception)
        {
            return FavoriteFailure.Describe(action, exception);
        }

        return "（操作未按预期失败）";
    }

    /// <summary>临时收藏库：用完即删，只读属性一并还原，避免残留文件影响后续用例。</summary>
    private sealed class TempFavoriteDatabase : IDisposable
    {
        public TempFavoriteDatabase(string name)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"nanikore-favorite-failure-{name}-{Guid.NewGuid():N}.db");
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (!File.Exists(Path))
                {
                    return;
                }

                File.SetAttributes(Path, FileAttributes.Normal);
                File.Delete(Path);
            }
            catch (IOException)
            {
                // 临时目录残留由操作系统清理，不影响断言。
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
