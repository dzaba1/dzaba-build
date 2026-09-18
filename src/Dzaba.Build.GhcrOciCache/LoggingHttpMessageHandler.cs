using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Dzaba.Build.GhcrOciCache;

/// <summary>
/// Logs every request/response this <see cref="GhcrOciCacheStorage"/> instance's <see cref="HttpClient"/>
/// sends - method, URI, status, elapsed time - at Debug, and failures at Warning. Never logs header
/// values, so the bearer token/Basic credential set on each request is never captured.
/// </summary>
internal sealed class LoggingHttpMessageHandler : DelegatingHandler
{
    private readonly ILogger logger;

    public LoggingHttpMessageHandler(ILogger logger, HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
        this.logger = logger;
    }

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        logger.LogDebug("GHCR {Method} {Uri}", request.Method, request.RequestUri);

        try
        {
            var response = base.Send(request, cancellationToken);
            logger.LogDebug(
                "GHCR {Method} {Uri} -> {StatusCode} ({ElapsedMs}ms)",
                request.Method, request.RequestUri, (int)response.StatusCode, stopwatch.ElapsedMilliseconds);
            return response;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "GHCR {Method} {Uri} failed after {ElapsedMs}ms", request.Method, request.RequestUri, stopwatch.ElapsedMilliseconds);
            throw;
        }
    }
}
