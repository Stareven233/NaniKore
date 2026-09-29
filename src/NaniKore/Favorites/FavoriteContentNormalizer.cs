namespace NaniKore.Favorites;

using NaniKore.Translation;

/// <summary>
/// 收藏内容归一化：剥离「漢字[かな]」「漢字 (かな)」注音形态后 trim，得到收藏的统一键。
/// 卡片收藏（词条净化后）、划词气泡（用户划到的可能是带注音原文）与历史脏行
/// （模型早期把注音写进词条，如 <c>気付く[きづく]</c>）在此收敛到同一形态，
/// 从源头杜绝重复行；匹配（IsFavorited / RemoveFavoriteAsync）同样以此为键。
/// </summary>
public static class FavoriteContentNormalizer
{
    /// <summary>
    /// 归一化收藏内容：空内容返回空串；否则剥离注音括号并 trim。
    /// 首段被剥离的读音在此丢弃——归一化只服务于「同一词条」身份判定，读音信息由 Note 列承载。
    /// </summary>
    public static string Normalize(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return string.Empty;
        }

        return RubyTextParser.StripRubyAnnotations(content, out _).Trim();
    }

    /// <summary>归一化后的相等比较（忽略大小写与首尾空白）。</summary>
    public static bool Equals(string? left, string? right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
}
