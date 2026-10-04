namespace NaniKore.Phonetics;

/// <summary>
/// 应用级注音注册表持有者：进程内单例。
/// 存在的理由：设置页下载/更新词典成功后必须重置标注器的「不可用」闩锁（热生效，无需重启），
/// 而结果面板（issue 008 Phase 3）读原文注音时也走同一实例——两处不能各建一份，
/// 否则下载后面板侧仍是旧的不可用探针。自测不经过本类，直接 new 独立实例，互不影响。
/// </summary>
internal static class PhoneticAnnotatorRegistryHolder
{
    /// <summary>默认三语装配；词典缺哪语哪语软降级（IsAvailable=false）。</summary>
    public static TextAnnotatorRegistry Default { get; } = TextAnnotatorRegistry.CreateDefault();

    /// <summary>下载落地后调用：清闩锁使下次探测重试；null/auto 时重置全部语言。</summary>
    public static void ResetAvailability(string? languageCode = null) => Default.ResetAvailability(languageCode);
}
