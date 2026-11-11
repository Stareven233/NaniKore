using System.Runtime.InteropServices;
using System.Text;
using NaniKore.Input;
using NaniKore.Interop;

namespace NaniKore.Diagnostics;

internal static partial class SelfTest
{
    [LibraryImport("shell32.dll", EntryPoint = "DragQueryFileW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint QueryDroppedFile(nint drop, uint index, [Out] char[]? path, uint capacity);

    /// <summary>真实 Win32 文件复制回归：不发送桌面按键，以无回应委托模拟不处理 Ctrl+C 的窗口。</summary>
    private static void TestFileClipboardRoundTrip()
    {
        var gateway = new Win32ClipboardGateway();
        using var original = gateway.Snapshot();
        const uint fileDropId = 15;
        string[] paths = [@"C:\NaniKore 自测\日本語.txt", @"C:\NaniKore 自测\second file.png"];

        // DROPFILES 为 20 字节固定头，pFiles 指向双零终止的 UTF-16 路径列表。
        var names = Encoding.Unicode.GetBytes(string.Join('\0', paths) + "\0\0");
        var dropBytes = new byte[20 + names.Length];
        BitConverter.GetBytes(20).CopyTo(dropBytes, 0);
        BitConverter.GetBytes(1).CopyTo(dropBytes, 16);
        names.CopyTo(dropBytes, 20);
        var effectId = NativeMethods.RegisterClipboardFormatW("Preferred DropEffect");
        var idListId = NativeMethods.RegisterClipboardFormatW("Shell IDList Array");
        // 空 CIDA（无子项，桌面 PIDL）：验证 Shell 格式的原始字节往返，不模拟文件传输。
        byte[] idList = [0, 0, 0, 0, 8, 0, 0, 0, 0, 0];

        void WriteBlock(uint id, byte[] bytes)
        {
            var memory = NativeMethods.GlobalAlloc(NativeMethods.GMEM_MOVEABLE, (nuint)bytes.Length);
            if (memory == 0) throw new ExternalException("测试数据分配失败。");
            var pointer = NativeMethods.GlobalLock(memory);
            if (pointer == 0)
            {
                NativeMethods.GlobalFree(memory);
                throw new ExternalException("测试数据锁定失败。");
            }
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            NativeMethods.GlobalUnlock(memory);
            if (NativeMethods.SetClipboardData(id, memory) == 0)
            {
                NativeMethods.GlobalFree(memory);
                throw new ExternalException("测试剪贴板写入失败。");
            }
        }

        void Seed(uint effect)
        {
            if (!NativeMethods.OpenClipboard(0)) throw new ExternalException("测试剪贴板无法打开。");
            try
            {
                if (!NativeMethods.EmptyClipboard()) throw new ExternalException("测试剪贴板无法清空。");
                WriteBlock(fileDropId, dropBytes);
                WriteBlock(effectId, BitConverter.GetBytes(effect));
                WriteBlock(idListId, idList);
            }
            finally { NativeMethods.CloseClipboard(); }
        }

        void CheckFiles(string label, uint effect)
        {
            if (!NativeMethods.OpenClipboard(0)) throw new ExternalException("测试剪贴板无法读取。");
            try
            {
                var drop = NativeMethods.GetClipboardData(fileDropId);
                var actual = new List<string>();
                var count = drop == 0 ? 0 : QueryDroppedFile(drop, uint.MaxValue, null, 0);
                for (uint index = 0; index < count; index++)
                {
                    var buffer = new char[QueryDroppedFile(drop, index, null, 0) + 1];
                    var length = QueryDroppedFile(drop, index, buffer, (uint)buffer.Length);
                    actual.Add(new string(buffer, 0, (int)length));
                }
                Check(label + " exposes original paths via DragQueryFileW", actual.SequenceEqual(paths));
                foreach (var (id, expected) in new[] { (effectId, BitConverter.GetBytes(effect)), (idListId, idList) })
                {
                    var memory = NativeMethods.GetClipboardData(id);
                    var pointer = memory == 0 ? 0 : NativeMethods.GlobalLock(memory);
                    var bytes = new byte[expected.Length];
                    if (pointer != 0)
                    {
                        try { Marshal.Copy(pointer, bytes, 0, bytes.Length); }
                        finally { NativeMethods.GlobalUnlock(memory); }
                    }
                    Check(label + $" preserves Shell format {id}", pointer != 0 && bytes.SequenceEqual(expected));
                }
            }
            finally { NativeMethods.CloseClipboard(); }
        }

        PasteInputRequest Request(Action copy)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            return new PasteInputRequest(
                ClipboardImageRoute.DirectFeed,
                _ => throw new InvalidOperationException("文件引用不应调用 OCR。"),
                gateway, copy, _ =>
                {
                    if (timer.Elapsed >= PasteInputAcquirer.PollTimeout) return false;
                    Thread.Sleep(PasteInputAcquirer.PollInterval);
                    return true;
                });
        }

        try
        {
            foreach (uint effect in new uint[] { 1, 2 }) // DROPEFFECT_COPY / MOVE
            {
                Seed(effect);
                var outcome = PasteInputAcquirer.Acquire(Request(() => { }));
                Check("file clipboard timeout returns empty without restore failure", outcome.Kind == PasteInputKind.Empty && outcome.Notice is null);
                CheckFiles($"file timeout (effect {effect})", effect);

                Seed(effect);
                outcome = PasteInputAcquirer.Acquire(Request(() => gateway.SetText(string.Empty)));
                Check("empty Ctrl+C response falls back without interpreting files as text", outcome.Kind == PasteInputKind.Empty);
                CheckFiles($"empty copy response (effect {effect})", effect);
            }

            Seed(1);
            var selection = PasteInputAcquirer.Acquire(Request(() => gateway.SetText("新选区")));
            using var text = gateway.Snapshot();
            Check("new Ctrl+C text wins over the original file clipboard", selection.Kind == PasteInputKind.SelectionText && text?.Text == "新选区");

            gateway.SetText("旧文本");
            var files = PasteInputAcquirer.Acquire(Request(() => Seed(2)));
            Check("new Ctrl+C files are left intact", files.Kind == PasteInputKind.Empty && files.Notice is not null);
            CheckFiles("new file copy", 2);
        }
        finally
        {
            // 文件回归失败也恢复用户快照；没有原内容时还原空快照。
            using var empty = new ClipboardSnapshot([], null, null);
            gateway.Restore(original ?? empty);
        }
    }
}
