using System.Runtime.InteropServices;
using System.IO.Compression;
using NaniKore.Phonetics;

namespace NaniKore.Diagnostics;

/// <summary>
/// 原生 ABI、下载与资源生命周期骨架（issue 009）的自测：
/// 清单/ABI 客户端/失败原因/闲置释放与重建/并发，全部在真实宿主进程内跑真实的正式语言包。
/// 临时报告写 .temp；隔离矩阵（008 的 verify 客户端）只作前置证据，不能替代这里的宿主验收。
/// </summary>
internal static partial class SelfTest
{
    private static void RunNativePhoneticPackTests()
    {
        TestNativePhoneticManifest();
        TestNativePhoneticPackStates();
        TestNativePhoneticAbiClient();
        TestNativePhoneticFailureReasons();
        TestNativePhoneticMissingPackDegradation();
        TestNativePhoneticInstallLifecycle();
        TestNativePhoneticIdleRelease();
        TestNativePhoneticConcurrency();
        TestNativePhoneticStateLabels();
    }

    /// <summary>身份清单：代码、库名、导出、许可与目录口径固定。</summary>
    private static void TestNativePhoneticManifest()
    {
        Check(
            "原生包清单：只装配 ja/en（中文走拼音数据文件）",
            NativePhoneticManifest.SupportedLanguages.SequenceEqual(["ja", "en"]) &&
            NativePhoneticManifest.IsSupported("ja") &&
            NativePhoneticManifest.IsSupported("JA") &&
            !NativePhoneticManifest.IsSupported("zh") &&
            !NativePhoneticManifest.IsSupported("ko") &&
            NativePhoneticManifest.GetPack("zh") is null &&
            NativePhoneticManifest.GetPack("") is null &&
            NativePhoneticManifest.GetPack(null) is null);

        foreach (var pack in NativePhoneticManifest.Packs)
        {
            Check(
                $"{pack.Language} 包身份：库名/字节/哈希/导出/许可",
                pack.LibraryFileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                pack.LibraryBytes > 0 &&
                pack.LibrarySha256.Length == 64 &&
                pack.Files.Count > 1 &&
                string.Equals(pack.Files[0].RelativePath, pack.LibraryFileName, StringComparison.Ordinal) &&
                string.Equals(pack.Files[0].Sha256, pack.LibrarySha256, StringComparison.Ordinal) &&
                pack.LicenseFiles.Any(file => file.RelativePath.StartsWith("licenses/", StringComparison.Ordinal)) &&
                pack.Exports.SequenceEqual([
                    NativePhoneticManifest.AbiVersionExport,
                    NativePhoneticManifest.AnnotateExport,
                    NativePhoneticManifest.ReleaseExport,
                    NativePhoneticManifest.FreeExport
                ]));
        }

        Check(
            "语言包目录与根目录整包直链：deps/phonetics/{ja,en} + ja.zip/en.zip",
            NativePhoneticManifest.GetPackDirectory("ja").EndsWith(Path.Combine("deps", "phonetics", "ja"), StringComparison.OrdinalIgnoreCase) &&
            NativePhoneticManifest.GetStagingDirectory("ja").EndsWith(Path.Combine("phonetics", ".staging-ja"), StringComparison.OrdinalIgnoreCase) &&
            (PhoneticDataPaths.ModelScopeBaseUrl + NativePhoneticManifest.ArchiveFileName("ja") + "?download=true").EndsWith("/phonetics/ja.zip?download=true", StringComparison.Ordinal));
    }

    /// <summary>
    /// 状态与磁盘判定必须同源：界面说「已就绪」的包必须真的能校验通过，缺/坏文件必须落到「缺失/需更新」。
    /// </summary>
    private static void TestNativePhoneticPackStates()
    {
        using var host = new NativePhoneticPackHost();
        foreach (var pack in NativePhoneticManifest.Packs)
        {
            var directory = NativePhoneticManifest.GetPackDirectory(pack.Language);
            if (!Directory.Exists(directory))
            {
                Check($"{pack.Language} 未安装时状态为缺失", host.GetState(pack.Language) == NativePhoneticPackState.Missing);
                Skip($"{pack.Language} 原生包未就绪（{directory}），跳过状态与金样本断言");
                continue;
            }

            var state = host.GetState(pack.Language);
            var verified = NativePhoneticPackVerifier.Verify(pack, directory, out var detail);
            Check(
                $"{pack.Language} 包状态与磁盘校验一致（状态={state}，校验={verified}{(detail is null ? "" : "：" + detail)}）",
                state switch
                {
                    NativePhoneticPackState.Ready or NativePhoneticPackState.RestartPending => verified == NativePhoneticPackageFailure.None,
                    NativePhoneticPackState.Missing => verified == NativePhoneticPackageFailure.Missing,
                    NativePhoneticPackState.NeedsUpdate => verified == NativePhoneticPackageFailure.HashMismatch,
                    NativePhoneticPackState.Failed => verified is not (NativePhoneticPackageFailure.None or NativePhoneticPackageFailure.Missing or NativePhoneticPackageFailure.HashMismatch),
                    _ => false,
                });
        }

        Check("中文与未知语言没有原生包状态", host.GetState("zh") == NativePhoneticPackState.Unsupported &&
            host.GetState("ko") == NativePhoneticPackState.Unsupported &&
            host.GetState("自动检测") == NativePhoneticPackState.Unsupported &&
            host.GetDetail("zh") is null);
        Check("无暂存包时提升为空操作", host.ApplyStagedPacks() == 0);
        Check("未知语言不能发起原生包下载", Throws<InvalidOperationException>(() => host.DownloadAsync("zh", null, CancellationToken.None).GetAwaiter().GetResult()));
    }

    /// <summary>ABI 客户端金样本：日语书面假名、英语 IPA 与候选、区间对齐、释放后重建一致。</summary>
    private static void TestNativePhoneticAbiClient()
    {
        TestNativePhoneticJapaneseClient();
        TestNativePhoneticEnglishClient();
    }

    private static void TestNativePhoneticJapaneseClient()
    {
        if (NativePhoneticPackVerifier.Verify(NativePhoneticManifest.GetPack("ja")!, NativePhoneticManifest.GetPackDirectory("ja"), out _) != NativePhoneticPackageFailure.None)
        {
            Skip("ja 原生包未就绪，跳过 ABI 客户端金样本");
            return;
        }

        if (!NativePhoneticLibrary.TryOpen("ja", out var library, out var failure, out var detail))
        {
            Check("ja 原生包应能加载", false, NativePhoneticPackageFailureText.Describe(failure, NativePhoneticManifest.GetPack("ja"), detail));
            return;
        }

        using (library)
        {
            Check("ja ABI 版本为 1", library!.AbiVersion == NativePhoneticManifest.AbiVersion);
            const string text = "東京へ行く。";
            // 冷加载发生在第一次真正触碰引擎的调用上（词表解析），实测值记入 issue 009 报告。
            var coldWatch = System.Diagnostics.Stopwatch.StartNew();
            var result = library.Annotate(text);
            coldWatch.Stop();
            Check("ja 注音调用成功", result.Succeeded, result.Error ?? string.Empty);
            Check(
                "ja 词元区间在原文中连续且与词面切片一致",
                result.Tokens.All(token => token.Start >= 0 && token.Length > 0 &&
                    token.Start + token.Length <= text.Length &&
                    text.AsSpan(token.Start, token.Length).SequenceEqual(token.Surface)));
            var tokyo = result.Tokens.FirstOrDefault(token => token.Surface == "東京");
            Check("ja 书面假名：東京 → トウキョウ（不是发音表记 トーキョー）", tokyo.Reading == "トウキョウ", $"actual={tokyo.Reading}");
            Check("ja 词数统计只认含汉字词面", new NativeTextAnnotator("ja").CountWords(text) == 2);

            var before = string.Join('|', result.Tokens.Select(token => $"{token.Start}:{token.Length}:{token.Reading}"));
            var warmWatch = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < 20; i++)
            {
                _ = library.Annotate(text);
            }

            warmWatch.Stop();
            Check("ja 释放返回成功", library.Release() == 0);
            var rebuildWatch = System.Diagnostics.Stopwatch.StartNew();
            var rebuiltTokens = library.Annotate(text).Tokens;
            rebuildWatch.Stop();
            var rebuilt = string.Join('|', rebuiltTokens.Select(token => $"{token.Start}:{token.Length}:{token.Reading}"));
            Console.WriteLine(
                $"[native] ja cold={coldWatch.Elapsed.TotalMilliseconds:F0}ms warm20={warmWatch.Elapsed.TotalMilliseconds:F1}ms " +
                $"rebuild={rebuildWatch.Elapsed.TotalMilliseconds:F0}ms 托管堆={GC.GetTotalMemory(false) / (1024 * 1024)}MB 工作集={Environment.WorkingSet / (1024 * 1024)}MB");
            Check("ja 释放后重建结果完全一致", string.Equals(before, rebuilt, StringComparison.Ordinal));
            Check("ja 重复释放不报错（幂等）", library.Release() == 0 && library.Release() == 0);
        }
    }

    private static void TestNativePhoneticEnglishClient()
    {
        if (NativePhoneticPackVerifier.Verify(NativePhoneticManifest.GetPack("en")!, NativePhoneticManifest.GetPackDirectory("en"), out _) != NativePhoneticPackageFailure.None)
        {
            Skip("en 原生包未就绪，跳过 ABI 客户端金样本");
            return;
        }

        if (!NativePhoneticLibrary.TryOpen("en", out var library, out var failure, out var detail))
        {
            Check("en 原生包应能加载", false, NativePhoneticPackageFailureText.Describe(failure, NativePhoneticManifest.GetPack("en"), detail));
            return;
        }

        using (library)
        {
            const string text = "The water is better in the city.";
            var coldWatch = System.Diagnostics.Stopwatch.StartNew();
            var result = library!.Annotate(text);
            coldWatch.Stop();
            Check("en 注音调用成功", result.Succeeded, result.Error ?? string.Empty);
            var water = result.Tokens.FirstOrDefault(token => token.Surface == "water");
            Check("en 词典式 IPA：water 有读音（闪音 ɾ 原样保留）", water.Reading == "ˈwɔːɾ.ər", $"actual={water.Reading}");
            Check(
                "en 候选首项即本句读音且去重",
                water.Candidates is { Count: > 0 } candidates &&
                candidates[0] == water.Reading &&
                candidates.Distinct(StringComparer.Ordinal).Count() == candidates.Count);
            Check(
                "en 数字与货币不注音",
                result.Tokens.Where(token => token.Surface.Any(char.IsDigit) || token.Surface == "$").All(token => token.Reading is null));
            Check(
                "en 词元区间与原文切片一致",
                result.Tokens.All(token => token.Length == token.Surface.Length &&
                    token.Start + token.Length <= text.Length &&
                    text.AsSpan(token.Start, token.Length).SequenceEqual(token.Surface)));

            // 冷/热/释放重建：与日语同一套观测口径（首调用含词表解析，热态是稳态单次成本）。
            var first = string.Join('|', result.Tokens.Select(token => $"{token.Start}:{token.Length}:{token.Reading}:{string.Join(',', token.Candidates ?? [])}"));
            var warmWatch = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < 20; i++)
            {
                _ = library.Annotate(text);
            }

            warmWatch.Stop();
            Check("en 释放返回成功", library.Release() == 0);
            var rebuildWatch = System.Diagnostics.Stopwatch.StartNew();
            var rebuiltResult = library.Annotate(text);
            rebuildWatch.Stop();
            var rebuilt = string.Join('|', rebuiltResult.Tokens.Select(token => $"{token.Start}:{token.Length}:{token.Reading}:{string.Join(',', token.Candidates ?? [])}"));
            Console.WriteLine(
                $"[native] en cold={coldWatch.Elapsed.TotalMilliseconds:F0}ms warm20={warmWatch.Elapsed.TotalMilliseconds:F1}ms " +
                $"rebuild={rebuildWatch.Elapsed.TotalMilliseconds:F0}ms 托管堆={GC.GetTotalMemory(false) / (1024 * 1024)}MB 工作集={Environment.WorkingSet / (1024 * 1024)}MB");
            Check("en 释放后重建的读音与候选完全一致", string.Equals(first, rebuilt, StringComparison.Ordinal));
            Check("en 重复释放不报错（幂等）", library.Release() == 0 && library.Release() == 0);
        }
    }

    /// <summary>
    /// 失败原因必须逐项可复现：缺包、哈希不符、清单不符、缺导出、PE 架构。
    /// 用临时目录造夹具，绝不改动 deps 下的正式包。
    /// </summary>
    private static void TestNativePhoneticFailureReasons()
    {
        var jaPack = NativePhoneticManifest.GetPack("ja")!;
        var root = Path.Combine(Path.GetTempPath(), "nani-kore-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // 1. 空目录 = 缺失
            Check(
                "缺包被判定为 Missing 而不是崩溃",
                !NativePhoneticLibrary.TryOpenAt("ja", root, out _, out var missing, out _) &&
                missing == NativePhoneticPackageFailure.Missing);

            // 2. 有文件但字节数/哈希不符 = 需要更新（下载中断或旧版本）
            File.WriteAllText(Path.Combine(root, jaPack.LibraryFileName), "not a real pack");
            File.WriteAllText(Path.Combine(root, NativePhoneticManifest.PackManifestFileName), "{}");
            Check(
                "哈希/字节数不符被判定为 HashMismatch",
                !NativePhoneticLibrary.TryOpenAt("ja", root, out _, out var mismatch, out _) &&
                mismatch == NativePhoneticPackageFailure.HashMismatch);
            Check(
                "非 PE 文件不能被当成 win-x64 包",
                !NativePhoneticPackVerifier.VerifyArchitecture(Path.Combine(root, jaPack.LibraryFileName), out _));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        // 3. 缺导出：用一个没有 nk_phonetics_* 的系统 DLL 验证拦截逻辑（不涉及正式包）
        var systemDll = Path.Combine(Environment.SystemDirectory, "kernel32.dll");
        if (NativeLibrary.TryLoad(systemDll, out var handle))
        {
            try
            {
                Check(
                    "缺少 ABI 导出时拒绝绑定",
                    !NativePhoneticLibrary.VerifyExports(handle, jaPack, out var exportFailure, out var exportDetail) &&
                    exportFailure == NativePhoneticPackageFailure.MissingExport &&
                    exportDetail == NativePhoneticManifest.AbiVersionExport);
            }
            finally
            {
                NativeLibrary.Free(handle);
            }
        }
        else
        {
            Skip("无法加载 kernel32.dll，跳过缺导出判定");
        }

        var packDirectory = NativePhoneticManifest.GetPackDirectory("ja");
        if (File.Exists(Path.Combine(packDirectory, jaPack.LibraryFileName)))
        {
            Check(
                "正式包通过 win-x64 PE 架构校验",
                NativePhoneticPackVerifier.VerifyArchitecture(Path.Combine(packDirectory, jaPack.LibraryFileName), out _));
        }

        // 4. 清单不符：把正式包整体复制到临时目录后只改 pack.json 的字节数记录
        if (File.Exists(Path.Combine(packDirectory, NativePhoneticManifest.PackManifestFileName)))
        {
            var copyRoot = Path.Combine(Path.GetTempPath(), "nani-kore-native-copy-" + Guid.NewGuid().ToString("N"));
            try
            {
                CopyDirectory(packDirectory, copyRoot);
                var manifestPath = Path.Combine(copyRoot, NativePhoneticManifest.PackManifestFileName);
                var json = File.ReadAllText(manifestPath).Replace($"\"bytes\": {jaPack.LibraryBytes}", "\"bytes\": 1", StringComparison.Ordinal);
                File.WriteAllText(manifestPath, json);
                var failure = NativePhoneticPackVerifier.Verify(jaPack, copyRoot, out _);
                Check("pack.json 与宿主清单不符被拒绝（ManifestMismatch 或哈希不符）",
                    failure is NativePhoneticPackageFailure.ManifestMismatch or NativePhoneticPackageFailure.HashMismatch,
                    $"actual={failure}");
            }
            finally
            {
                Directory.Delete(copyRoot, recursive: true);
            }
        }
    }

    /// <summary>没有原生包的语言（中文）必须安静退化为纯文本，不让异常冒到翻译主链路。</summary>
    private static void TestNativePhoneticMissingPackDegradation()
    {
        using var unsupported = new NativeTextAnnotator("zh");
        var spans = unsupported.Annotate("東京へ行く", new AnnotationContext(AnnotationProfile.Load("ja", rareOnly: false)));
        Check(
            "无原生包的语言：不可用、注音为空、不抛异常",
            !unsupported.IsAvailable &&
            spans.Count == 0 &&
            unsupported.CountWords("東京へ行く") == 0 &&
            unsupported.Failure == NativePhoneticPackageFailure.Unsupported);
    }

    /// <summary>
    /// 安装生命周期全链（临时目录 + 本地复制代替网络）：缺失 → 下载失败可重试 → 安装成功 →
    /// 已加载时新版本只暂存 → 重启后提升 → 取消下载。不联网、不碰 deps 下的正式包。
    /// 提升（PromoteStaged）必须用未在本进程加载过的包来验：已加载的 NativeAOT DLL 在进程内不可卸载，
    /// Windows 会拒绝覆盖它的文件，而真实「重启」是另一个进程，不存在这个困扰——所以暂存/提升用 en，
    /// 首次安装与真实注音用 ja。
    /// </summary>
    private static void TestNativePhoneticInstallLifecycle()
    {
        var jaPack = NativePhoneticManifest.GetPack("ja")!;
        var enPack = NativePhoneticManifest.GetPack("en")!;
        var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [jaPack.Language] = NativePhoneticManifest.GetPackDirectory(jaPack.Language),
            [enPack.Language] = NativePhoneticManifest.GetPackDirectory(enPack.Language)
        };
        if (sources.Any(source => NativePhoneticPackVerifier.Verify(NativePhoneticManifest.GetPack(source.Key)!, source.Value, out _) != NativePhoneticPackageFailure.None))
        {
            Skip("ja/en 正式包未就绪，跳过安装生命周期断言");
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "nani-kore-native-lifecycle-" + Guid.NewGuid().ToString("N"));
        string Target(string language) => Path.Combine(root, "pack", language);
        string Staging(string language) => Path.Combine(root, "staging", language);
        var failNext = true;
        // 模拟一个刚启动的宿主进程：没有语言被加载过，「首次安装即时生效」才有意义。
        NativePhoneticLibrary.ResetLoadedLanguagesForTests();
        try
        {
            // 下载器把正式包压成单个 ZIP：模拟整包 HTTP 下载，不依赖网络。
            var installer = new NativePhoneticPackInstaller(
                Staging,
                Target,
                (url, destination, _, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (failNext)
                    {
                        throw new HttpRequestException("模拟网络失败");
                    }

                    const string marker = "/phonetics/";
                    var tail = url[(url.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..];
                    tail = tail[..tail.IndexOf('?')];
                    var language = Path.GetFileNameWithoutExtension(tail);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    ZipFile.CreateFromDirectory(sources[language], destination, CompressionLevel.Optimal, includeBaseDirectory: false);
                    return Task.CompletedTask;
                });

            using var host = new NativePhoneticPackHost(installer: installer, packDirectory: Target);
            Check("首次安装前状态为缺失", host.GetState("ja") == NativePhoneticPackState.Missing);

            Check(
                "下载失败被捕获、状态置为失败并记录原因",
                Throws<HttpRequestException>(() => host.DownloadAsync("ja", null, CancellationToken.None).GetAwaiter().GetResult()) &&
                host.GetState("ja") == NativePhoneticPackState.Failed &&
                (host.GetDetail("ja") ?? string.Empty).Contains("模拟网络失败", StringComparison.Ordinal),
                $"state={host.GetState("ja")} detail={host.GetDetail("ja")}");

            failNext = false;
            Check(
                "失败后重试可安装成功（首次安装即时生效）",
                host.DownloadAsync("ja", null, CancellationToken.None).GetAwaiter().GetResult() &&
                host.GetState("ja") == NativePhoneticPackState.Ready &&
                !Directory.Exists(Staging("ja")),
                $"state={host.GetState("ja")}");

            var annotator = host.GetAnnotator("ja")!;
            Check("安装后的语言包可以真实注音", annotator.IsAvailable && annotator.CountWords("東京へ行く") == 2);

            // en 在本进程里先真正加载一次正式包（模拟「该语言已在使用」），后续新版本就只会落暂存。
            if (NativePhoneticLibrary.TryOpen(enPack.Language, out var loadedEnglish, out _, out _))
            {
                loadedEnglish!.Dispose();
                Check("已加载时新版本只暂存并提示重启生效",
                    host.DownloadAsync(enPack.Language, null, CancellationToken.None).GetAwaiter().GetResult() &&
                    host.GetState(enPack.Language) == NativePhoneticPackState.RestartPending &&
                    Directory.Exists(Staging(enPack.Language)),
                    $"state={host.GetState(enPack.Language)}");

                // 再塞一份坏暂存包：提升时必须整份丢弃，绝不用来覆盖正式包。
                var brokenStaging = Staging(jaPack.Language);
                Directory.CreateDirectory(brokenStaging);
                File.WriteAllText(Path.Combine(brokenStaging, jaPack.LibraryFileName), "not a real pack");
                File.WriteAllText(Path.Combine(brokenStaging, NativePhoneticManifest.PackManifestFileName), "{}");

                Check(
                    "重启时把有效暂存包提升为正式包、坏包整份丢弃",
                    host.ApplyStagedPacks() == 1 &&
                    host.GetState(enPack.Language) == NativePhoneticPackState.Ready &&
                    !Directory.Exists(Staging(enPack.Language)) &&
                    !Directory.Exists(brokenStaging) &&
                    NativePhoneticPackVerifier.Verify(jaPack, Target(jaPack.Language), out _) == NativePhoneticPackageFailure.None,
                    $"en={host.GetState(enPack.Language)} 坏暂存包残留={Directory.Exists(brokenStaging)}");
            }
            else
            {
                Skip("en 正式包未能加载，跳过暂存与重启提升断言");
            }

            using var cancelHost = new NativePhoneticPackHost(
                installer: new NativePhoneticPackInstaller(language => Staging(language) + "-cancel", language => Target(language) + "-cancel"),
                packDirectory: language => Target(language) + "-cancel");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Check(
                "取消下载保留失败状态与重试入口",
                Throws<OperationCanceledException>(() => cancelHost.DownloadAsync("ja", null, cancellation.Token).GetAwaiter().GetResult()) &&
                cancelHost.GetState("ja") == NativePhoneticPackState.Failed &&
                (cancelHost.GetDetail("ja") ?? string.Empty).Contains("取消", StringComparison.Ordinal),
                $"state={cancelHost.GetState("ja")} detail={cancelHost.GetDetail("ja")}");
        }
        finally
        {
            // 临时目录里的 DLL 已被加载（NativeAOT 不可卸载），删除可能失败；失败也不影响正式包。
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// 闲置释放接到现有的惰性资源（IdleResource）上：到期后释放原生词表，下一次访问自动重建且结果一致。
    /// DLL 常驻不卸载、不强制 GC，这是「释放数据」而不是「卸载模块」的验收点。
    /// </summary>
    private static void TestNativePhoneticIdleRelease()
    {
        if (NativePhoneticPackVerifier.Verify(NativePhoneticManifest.GetPack("ja")!, NativePhoneticManifest.GetPackDirectory("ja"), out _) != NativePhoneticPackageFailure.None)
        {
            Skip("ja 原生包未就绪，跳过闲置释放断言");
            return;
        }

        var clock = new ResourceTestClock();
        using var host = new NativePhoneticPackHost(TimeSpan.Zero, clock);
        var annotator = host.GetAnnotator("ja")!;
        host.ConfigureIdleTimeout(TimeSpan.FromSeconds(30));

        const string text = "薬の過剰摂取";
        var first = annotator.Annotate(text, new AnnotationContext(AnnotationProfile.Load("ja", rareOnly: false)));
        Check("闲置计时：首次注音产出区间", first.Count > 0, $"spans={first.Count}");
        Check("闲置计时：未到期不释放", host.ReleaseIdleResources() == 0);

        clock.Advance(TimeSpan.FromSeconds(31));
        Check("闲置到期释放原生词表", host.ReleaseIdleResources() == 1);
        var rebuilt = annotator.Annotate(text, new AnnotationContext(AnnotationProfile.Load("ja", rareOnly: false)));
        Check(
            "释放后重建给出完全一致的区间",
            rebuilt.Count == first.Count &&
            rebuilt.Zip(first).All(pair => pair.First.Start == pair.Second.Start && pair.First.Length == pair.Second.Length && pair.First.Reading == pair.Second.Reading),
            $"first={AnnotatedTextFormatter.Compose(text, first)} rebuilt={AnnotatedTextFormatter.Compose(text, rebuilt)}");
        Check("闲置回收后文本仍可正常处理", annotator.CountWords(text) > 0);
    }

    /// <summary>并发首建与「解析中回收」：注音进行中请求回收不得释放正在使用的词表。</summary>
    private static void TestNativePhoneticConcurrency()
    {
        if (NativePhoneticPackVerifier.Verify(NativePhoneticManifest.GetPack("ja")!, NativePhoneticManifest.GetPackDirectory("ja"), out _) != NativePhoneticPackageFailure.None)
        {
            Skip("ja 原生包未就绪，跳过并发断言");
            return;
        }

        using var host = new NativePhoneticPackHost();
        var annotator = host.GetAnnotator("ja")!;
        var passed = new bool[8];
        Parallel.For(0, 8, index =>
        {
            if (index == 3)
            {
                // 与注音并发地请求回收：IdleResource 用 TryEnter，绝不会在调用途中释放。
                _ = host.ReleaseIdleResources();
                passed[index] = true;
                return;
            }

            passed[index] = annotator.Annotate("東京へ行く", new AnnotationContext(AnnotationProfile.Load("ja", rareOnly: false))).Count > 0;
        });

        Check("8 路并发注音（含并发回收）全部成功", passed.All(static ok => ok));
        Check("并发后状态仍为就绪", host.GetState("ja") is NativePhoneticPackState.Ready or NativePhoneticPackState.RestartPending);
    }

    /// <summary>状态 → 界面文案必须覆盖全部状态（设置页按钮与自测共用）。</summary>
    private static void TestNativePhoneticStateLabels()
    {
        Check(
            "原生包状态文案覆盖全部状态",
            Enum.GetValues<NativePhoneticPackState>().All(state =>
                !string.IsNullOrWhiteSpace(NativePhoneticPackStateText.ButtonLabel(state)) &&
                !string.IsNullOrWhiteSpace(NativePhoneticPackStateText.Description(state))));
        Check(
            "原生包状态文案：重启生效与失败可区分",
            NativePhoneticPackStateText.ButtonLabel(NativePhoneticPackState.RestartPending) == "重启生效" &&
            NativePhoneticPackStateText.ButtonLabel(NativePhoneticPackState.Failed) == "重试" &&
            NativePhoneticPackStateText.ButtonLabel(NativePhoneticPackState.Ready) == "已就绪");
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static bool Throws<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
            return false;
        }
        catch (TException)
        {
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
