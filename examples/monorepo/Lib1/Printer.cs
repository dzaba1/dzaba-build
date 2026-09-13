using System;
using System.Collections;
using System.Reflection;
using System.Text;
using Serilog;
using Serilog.Core;

namespace Lib1;

public static class Printer
{
    public static Logger GetLogger()
    {
        return new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console()
            .CreateLogger();
    }

    public static string GetAssemblyString(Type type, string framework)
    {
        var assemblyName = type.Assembly.GetName().Name;
        var fileVersion = type.Assembly
            .GetCustomAttribute<AssemblyFileVersionAttribute>()
            ?.Version ?? "unknown";
        return $"{type.FullName}, Assembly={assemblyName}, FileVersion={fileVersion}, TargetFramework={framework}";
    }

    public static string GetFullAssemblyString(int indent, Type type, string framework, params Func<int, string>[] referenceCallers)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"{new string(' ', indent)}{GetAssemblyString(type, framework)}");

        indent += 2;
        foreach (var caller in referenceCallers)
        {
            builder.AppendLine(caller(indent));
        }

        return builder.ToString().TrimEnd();
    }
}