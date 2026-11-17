namespace NaniKore.Favorites;

using System;
using System.IO;
using Microsoft.Data.Sqlite;

/// <summary>
/// 收藏库异常的面向用户描述：把 SQLite/IO 的英文异常收敛成可理解的中文原因与恢复建议。
/// 只服务于 UI 提示，不改变服务契约，也不把「读不到」当成「没有收藏」参与业务判断。
/// </summary>
public static class FavoriteFailure
{
    /// <summary>生成「{动作}失败：{原因}（{建议}）。」形式的提示文案。</summary>
    public static string Describe(string action, Exception exception)
    {
        // 错误码取自 SQLite 主结果码：5/6 占用、8 只读、11/26 损坏或非数据库、14 无法打开。
        var (reason, hint) = exception switch
        {
            SqliteException { SqliteErrorCode: 5 or 6 } => ("收藏数据库正被其他程序占用", "请关闭占用 collections.db 的程序后重试"),
            SqliteException { SqliteErrorCode: 11 or 26 } => ("收藏数据库文件已损坏", "请备份后重置 .data/collections.db 再重试"),
            SqliteException { SqliteErrorCode: 8 } => ("收藏数据库为只读", "请去掉 .data/collections.db 的只读属性后重试"),
            SqliteException { SqliteErrorCode: 14 } => ("无法打开收藏数据库文件", "请检查 .data/collections.db 是否被其他程序独占后重试"),
            UnauthorizedAccessException => ("没有访问收藏数据库的权限", "请检查 .data 目录的文件权限后重试"),
            IOException => ("收藏数据库文件不可读写", "请检查 .data/collections.db 是否被占用后重试"),
            SqliteException => ("收藏数据库读写失败", "请稍后重试"),
            _ => ("收藏数据读写失败", "请稍后重试"),
        };

        return $"{action}失败：{reason}（{hint}）";
    }
}
