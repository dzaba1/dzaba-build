using FrameworkGetter;

namespace Lib1;

public static class AssemblyPrinter
{
    public static string GetFullAssemblyString(int indent)
    {
        SerilogLog();

        var str = Printer.GetFullAssemblyString(indent, typeof(AssemblyPrinter), Framework.GetCurrentFramework());
        return $"{new string(' ', indent)}{str}";
    }

    private static void SerilogLog()
    {
        using var logger = Printer.GetLogger();
        logger.Information("Serilog is working in Lib1.AssemblyPrinter.SerilogLog");
    }
}