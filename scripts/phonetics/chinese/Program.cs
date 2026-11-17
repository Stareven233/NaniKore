using NaniKore.Phonetics;

namespace NaniKore.Phonetics.Build;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length != 1 || !Directory.Exists(Path.Combine(args[0], "resources", "phonetics")))
        {
            Console.Error.WriteLine("用法：ChinesePinyinDataBuilder <仓库根目录>");
            return 2;
        }

        try
        {
            ChinesePinyinDataBuilder.Build(Path.GetFullPath(args[0]));
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"中文拼音数据构建失败：{exception.Message}");
            return 1;
        }
    }
}
