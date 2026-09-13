using FrameworkGetter;
using Lib1;

namespace Lib10;

public static class AssemblyPrinter
{
    public static string GetFullAssemblyString(int indent)
    {
        var str = Printer.GetFullAssemblyString(indent, typeof(AssemblyPrinter), Framework.GetCurrentFramework(),
            Lib2.AssemblyPrinter.GetFullAssemblyString,
            Lib3.AssemblyPrinter.GetFullAssemblyString,
            Lib5.AssemblyPrinter.GetFullAssemblyString);
        return $"{new string(' ', indent)}{str}";
    }
}