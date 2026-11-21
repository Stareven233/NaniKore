using System.Reflection;

namespace NaniKore.Config;

internal static class ProductVersion
{
    public static string Current => typeof(ProductVersion).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? throw new InvalidOperationException("项目未设置程序集产品版本");
}
