using FrameworkGetter;
using Lib1;

namespace Lib5;

public static class AssemblyPrinter
{
    public static string GetFullAssemblyString(int indent)
    {
        var str = Printer.GetFullAssemblyString(indent, typeof(AssemblyPrinter), Framework.GetCurrentFramework(),
            i => Lib1.AssemblyPrinter.GetFullAssemblyString(i));
        return $"{new string(' ', indent)}{str}";
    }
}