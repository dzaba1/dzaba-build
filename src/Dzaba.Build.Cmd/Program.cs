using System.CommandLine;
using Dzaba.Build.Cmd.Commands;
using Dzaba.Build.Lib;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .CreateLogger();

try
{
    var services = new ServiceCollection();
    services.AddLogging(builder => builder.AddSerilog(dispose: true));
    services.AddDzabaBuildLib();

    using var serviceProvider = services.BuildServiceProvider();

    var rootCommand = new RootCommand("dzaba-build - build tooling utilities.")
    {
        HashCommand.Build(serviceProvider)
    };

    return rootCommand.Parse(args).Invoke();
}
finally
{
    Log.CloseAndFlush();
}
