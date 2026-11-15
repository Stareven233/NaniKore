// 把 MisakiSharp 包成原生 DLL 的 C ABI 导出（穿刺用）。
// 调用约定：UTF-8 字符串；返回值为 malloc 的 UTF-8 指针，调用方用 misaki_free 释放。
using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using MisakiSharp;

namespace MisakiNative;

public static unsafe class Exports
{
    static readonly EnglishG2P En = new(); // 单例；AOT 下静态字段，进程内复用

    // 健全性探针
    [UnmanagedCallersOnly(EntryPoint = "misaki_ping")]
    public static int Ping() => 42;

    // 中文注音符号串（仅验证跨边界；生产要走中间拼音）
    [UnmanagedCallersOnly(EntryPoint = "misaki_zh_phonemes")]
    public static IntPtr ZhPhonemes(IntPtr textUtf8)
        => Return(ChineseG2P.Phonemize(Ptr(textUtf8)));

    // 日语逐词 Surface+Reading（JSON）
    [UnmanagedCallersOnly(EntryPoint = "misaki_ja_tokens")]
    public static IntPtr JaTokens(IntPtr textUtf8)
    {
        try
        {
            var sb = new StringBuilder("[");
            bool first = true;
            foreach (var r in JapaneseTagger.Parse(Ptr(textUtf8)))
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"s\":\"").Append(Esc(r.Surface)).Append("\",\"r\":\"").Append(Esc(r.Reading)).Append("\"}");
            }
            return Return(sb.Append(']').ToString());
        }
        catch (Exception e) { return Return("{\"error\":\"" + Esc(e.Message) + "\"}"); }
    }

    // 英语逐词 Text+Phonemes+Rating（JSON）
    [UnmanagedCallersOnly(EntryPoint = "misaki_en_tokens")]
    public static IntPtr EnTokens(IntPtr textUtf8)
    {
        try
        {
            var (_, toks) = En.Phonemize(Ptr(textUtf8));
            var sb = new StringBuilder("[");
            for (int i = 0; i < toks.Count; i++)
            {
                var t = toks[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"t\":\"").Append(Esc(t.Text)).Append("\",\"p\":\"").Append(Esc(t.Phonemes ?? "")).Append("\",\"rate\":").Append(t.Rating?.ToString() ?? "null").Append('}');
            }
            return Return(sb.Append(']').ToString());
        }
        catch (Exception e) { return Return("{\"error\":\"" + Esc(e.Message) + "\"}"); }
    }

    [UnmanagedCallersOnly(EntryPoint = "misaki_free")]
    public static void Free(IntPtr p) { if (p != IntPtr.Zero) NativeMemory.Free((void*)p); }

    static string Ptr(IntPtr p) => Marshal.PtrToStringUTF8(p) ?? "";
    static IntPtr Return(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        var mem = (byte*)NativeMemory.Alloc((nuint)(bytes.Length + 1));
        fixed (byte* src = bytes) Buffer.MemoryCopy(src, mem, bytes.Length, bytes.Length);
        mem[bytes.Length] = 0;
        return (IntPtr)mem;
    }
    static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", " ");
}
