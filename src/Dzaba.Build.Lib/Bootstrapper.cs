using System;
using Dzaba.Build.Lib.Hashing;
using Microsoft.Extensions.DependencyInjection;

namespace Dzaba.Build.Lib;

public static class Bootstrapper
{
    public static IServiceCollection AddDzabaBuildLib(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddTransient<IFileHasher, FileHasher>();

        return services;
    }
}