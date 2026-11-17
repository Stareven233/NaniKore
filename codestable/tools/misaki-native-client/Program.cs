// AOT 客户端：运行时 LoadLibrary 动态加载 native DLL，验证「主程序小 + 词典外置 + 懒加载」可行。
// 本工程【不引用 MisakiSharp】，只 P/Invoke。
using System.Runtime.InteropServices;
using System.Text;

static class Program
{
    const string Dll = "MisakiNative.dll";

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr LoadLibrary(string path);
    [DllImport("kernel32", SetLastError = true)]
    static extern IntPtr GetProcAddress(IntPtr h, string name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int PingFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr StrFn(IntPtr text);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void FreeFn(IntPtr p);

    static void Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("[client] 启动，尚未加载 DLL");

        IntPtr h = LoadLibrary(Dll);
        Console.WriteLine($"[client] LoadLibrary({Dll}) = 0x{h:X} err={Marshal.GetLastWin32Error()}");
        if (h == IntPtr.Zero) { Console.WriteLine("[client] 加载失败"); return; }

        T Bind<T>(string n) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(GetProcAddress(h, n));

        var ping = Bind<PingFn>("misaki_ping");
        var zh = Bind<StrFn>("misaki_zh_phonemes");
        var ja = Bind<StrFn>("misaki_ja_tokens");
        var en = Bind<StrFn>("misaki_en_tokens");
        var free = Bind<FreeFn>("misaki_free");

        string Call(StrFn f, string s)
        {
            var inp = Marshal.StringToCoTaskMemUTF8(s);
            try { var p = f(inp); var r = Marshal.PtrToStringUTF8(p) ?? ""; free(p); return r; }
            finally { Marshal.FreeCoTaskMem(inp); }
        }

        Console.WriteLine($"[client] ping = {ping()}");
        Console.WriteLine($"[client] zh  = {Call(zh, "你好，世界！")}");
        Console.WriteLine($"[client] ja  = {Call(ja, "睡眠薬を過剰摂取すると致死量になる")}");
        Console.WriteLine($"[client] en  = {Call(en, "They will record a new record tomorrow.")}");
        Console.WriteLine("[client] 完成（若以上有值，说明 66MB 词典可外置为原生 DLL、主程序可很小）");
    }
}
