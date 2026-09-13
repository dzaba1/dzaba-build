using System;
using FrameworkGetter;
using Lib1;

namespace App1;

class Program
{
    static void Main(string[] args)
    {
        var result = GetFullAssemblyString();
        Console.WriteLine(result);
    }

    private static string GetFullAssemblyString()
    {
        var indent = 0;
        var str = Printer.GetFullAssemblyString(indent, typeof(AssemblyPrinter), Framework.GetCurrentFramework(),
            Lib7.AssemblyPrinter.GetFullAssemblyString,
            Lib10.AssemblyPrinter.GetFullAssemblyString);
        return $"{new string(' ', indent)}{str}";
    }
}