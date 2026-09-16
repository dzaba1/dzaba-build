using FrameworkGetter;
using Lib1;

namespace Lib11;

public static class AssemblyPrinter
{
    public static string GetFullAssemblyString(int indent)
    {
        var str = Printer.GetFullAssemblyString(indent, typeof(AssemblyPrinter), Framework.GetCurrentFramework(),
            Lib4.AssemblyPrinter.GetFullAssemblyString,
            Lib5.AssemblyPrinter.GetFullAssemblyString);
        return $"{new string(' ', indent)}{str}";
    }
}