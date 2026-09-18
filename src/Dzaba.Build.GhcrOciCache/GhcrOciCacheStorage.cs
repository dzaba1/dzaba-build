using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dzaba.Build.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dzaba.Build.GhcrOciCache;

/// <summary>
/// GitHub Container Registry (ghcr.io) backed <see cref="ICacheStorage"/> implementation - stores
/// each cache entry as a single-layer OCI artifact (manifest + zip blob), tagged by cache key, so
/// separate GitHub Actions runners can share cache entries over the network (unlike
/// Dzaba.Build.FileCache, which only works on a single machine).
///
/// Publishes push both blobs (the fixed empty config blob and the zip layer) before the manifest -
/// the manifest is the completion marker, mirroring FileCacheStorage's temp-dir-then-atomic-rename:
/// a reader can never observe a manifest whose blobs are missing.
/// </summary>
public sealed class GhcrOciCacheStorage : ICacheStorage
{
    private const string ManifestMediaType = "application/vnd.oci.image.manifest.v1+json";
    private const string EmptyConfigMediaType = "application/vnd.oci.empty.v1+json";
    private const string LayerMediaType = "application/vnd.dzaba.build.cache.layer.v1+zip";
    private static readonly DateTime ZipEntryTimestamp = new(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly byte[] EmptyConfigBytes = Encoding.UTF8.GetBytes("{}");
    private static readonly string EmptyConfigDigest = "sha256:" + ToHex(SHA256.HashData(EmptyConfigBytes));

    private readonly string registryHost;
    private readonly string repository;
    private readonly HttpClient http;
    private readonly ILogger<GhcrOciCacheStorage> logger;

    private readonly object tokenLock = new();
    private string bearerToken;
    private DateTimeOffset tokenExpiresAt;

    public GhcrOciCacheStorage(string imageReference)
    {
        (registryHost, repository) = ParseImageReference(imageReference);
        logger = CacheStorageLoggerRegistry.Current?.CreateLogger<GhcrOciCacheStorage>()
            ?? NullLogger<GhcrOciCacheStorage>.Instance;
        http = new HttpClient(new LoggingHttpMessageHandler(logger, new HttpClientHandler()));
    }

    public bool Exists(string cacheKey)
    {
        var tag = ToTag(cacheKey);
        using var response = SendAuthorized(() => NewManifestRequest(HttpMethod.Get, tag));
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            logger.LogInformation("{Repository}:{Tag} - cache MISS.", repository, tag);
            return false;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw Fail(HttpMethod.Get, ManifestUri(tag), response);
        }

        logger.LogInformation("{Repository}:{Tag} - cache HIT.", repository, tag);
        return true;
    }

    public void Fetch(string cacheKey, string destinationDirectory)
    {
        var tag = ToTag(cacheKey);
        var layerDigest = GetLayerDigest(tag);

        Directory.CreateDirectory(destinationDirectory);

        var tempZip = Path.Combine(Path.GetTempPath(), "dzaba-ghcr-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            using (var response = SendAuthorized(
                () => new HttpRequestMessage(HttpMethod.Get, BlobUri(layerDigest)),
                HttpCompletionOption.ResponseHeadersRead))
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw Fail(HttpMethod.Get, BlobUri(layerDigest), response);
                }

                using var fileStream = File.Create(tempZip);
                using var contentStream = response.Content.ReadAsStream();
                contentStream.CopyTo(fileStream);
            }

            ExtractFlat(tempZip, destinationDirectory);
            logger.LogInformation("{Repository}:{Tag} - fetched into {Destination}.", repository, tag, destinationDirectory);
        }
        finally
        {
            File.Delete(tempZip);
        }
    }

    public void Publish(string cacheKey, string sourceDirectory)
    {
        var tag = ToTag(cacheKey);

        using (var probe = SendAuthorized(() => NewManifestRequest(HttpMethod.Get, tag)))
        {
            if (probe.IsSuccessStatusCode)
            {
                logger.LogInformation("{Repository}:{Tag} - already published, skipping.", repository, tag);
                return;
            }
        }

        var tempZip = CreateDeterministicZip(sourceDirectory);
        try
        {
            var layerDigest = "sha256:" + ComputeSha256Hex(tempZip);
            var layerSize = new FileInfo(tempZip).Length;

            EnsureBlob(EmptyConfigDigest, () => new MemoryStream(EmptyConfigBytes), EmptyConfigBytes.Length);
            EnsureBlob(layerDigest, () => File.OpenRead(tempZip), layerSize);

            var manifestBytes = BuildManifestBytes(layerDigest, layerSize, cacheKey);
            using var manifestResponse = SendAuthorized(() =>
            {
                var request = NewManifestRequest(HttpMethod.Put, tag);
                request.Content = new ByteArrayContent(manifestBytes);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue(ManifestMediaType);
                return request;
            });

            if (!manifestResponse.IsSuccessStatusCode)
            {
                throw Fail(HttpMethod.Put, ManifestUri(tag), manifestResponse);
            }

            logger.LogInformation("{Repository}:{Tag} - published ({Size} bytes).", repository, tag, layerSize);
        }
        finally
        {
            File.Delete(tempZip);
        }
    }

    private string GetLayerDigest(string tag)
    {
        using var response = SendAuthorized(() => NewManifestRequest(HttpMethod.Get, tag));
        if (!response.IsSuccessStatusCode)
        {
            throw Fail(HttpMethod.Get, ManifestUri(tag), response);
        }

        using var doc = JsonDocument.Parse(response.Content.ReadAsStream());
        var layers = doc.RootElement.GetProperty("layers");
        if (layers.GetArrayLength() != 1)
        {
            throw new InvalidOperationException(
                $"GHCR manifest for tag '{tag}' in '{repository}' has {layers.GetArrayLength()} layers; expected exactly 1.");
        }

        return layers[0].GetProperty("digest").GetString();
    }

    private void EnsureBlob(string digest, Func<Stream> contentFactory, long length)
    {
        using (var head = SendAuthorized(() => new HttpRequestMessage(HttpMethod.Head, BlobUri(digest))))
        {
            if (head.IsSuccessStatusCode)
            {
                logger.LogDebug("{Repository} - blob {Digest} already present, skipping upload.", repository, digest);
                return;
            }
        }

        Uri uploadUri;
        using (var initiate = SendAuthorized(() => new HttpRequestMessage(HttpMethod.Post, UploadsUri())))
        {
            if (initiate.StatusCode != HttpStatusCode.Accepted || initiate.Headers.Location == null)
            {
                throw Fail(HttpMethod.Post, UploadsUri(), initiate);
            }

            uploadUri = initiate.Headers.Location.IsAbsoluteUri
                ? initiate.Headers.Location
                : new Uri(new Uri($"https://{registryHost}/"), initiate.Headers.Location);
        }

        var separator = uploadUri.Query.Length > 0 ? "&" : "?";
        var putUri = new Uri(uploadUri + separator + "digest=" + Uri.EscapeDataString(digest));

        using var put = SendAuthorized(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Put, putUri)
            {
                Content = new StreamContent(contentFactory())
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            request.Content.Headers.ContentLength = length;
            return request;
        });

        if (!put.IsSuccessStatusCode)
        {
            throw Fail(HttpMethod.Put, putUri, put);
        }

        logger.LogDebug("{Repository} - uploaded blob {Digest} ({Length} bytes).", repository, digest, length);
    }

    private HttpRequestMessage NewManifestRequest(HttpMethod method, string tag)
    {
        var request = new HttpRequestMessage(method, ManifestUri(tag));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(ManifestMediaType));
        return request;
    }

    private Uri ManifestUri(string tag) => new($"https://{registryHost}/v2/{repository}/manifests/{tag}");

    private Uri BlobUri(string digest) => new($"https://{registryHost}/v2/{repository}/blobs/{digest}");

    private Uri UploadsUri() => new($"https://{registryHost}/v2/{repository}/blobs/uploads/");

    private HttpResponseMessage SendAuthorized(
        Func<HttpRequestMessage> requestFactory,
        HttpCompletionOption option = HttpCompletionOption.ResponseContentRead)
    {
        using (var request = requestFactory())
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GetToken(forceRefresh: false));
            var response = http.Send(request, option);
            if (response.StatusCode != HttpStatusCode.Unauthorized)
            {
                return response;
            }

            logger.LogDebug("{Repository} - got 401, refreshing token and retrying once.", repository);
            response.Dispose();
        }

        var retryRequest = requestFactory();
        retryRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GetToken(forceRefresh: true));
        return http.Send(retryRequest, option);
    }

    private string GetToken(bool forceRefresh)
    {
        lock (tokenLock)
        {
            if (!forceRefresh && bearerToken != null && DateTimeOffset.UtcNow < tokenExpiresAt)
            {
                return bearerToken;
            }

            var password = Environment.GetEnvironmentVariable("DZABA_GHCR_TOKEN")
                ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN");
            if (string.IsNullOrEmpty(password))
            {
                throw new InvalidOperationException(
                    "GhcrOciCacheStorage requires a GITHUB_TOKEN (or DZABA_GHCR_TOKEN) environment variable " +
                    "with package read/write permission. In GitHub Actions, add 'permissions: packages: write' " +
                    "to the job and 'env: GITHUB_TOKEN: ${{ secrets.GITHUB_TOKEN }}' to the build step.");
            }

            var username = Environment.GetEnvironmentVariable("GITHUB_ACTOR") ?? "dzaba-build";
            var tokenUri = new Uri(
                $"https://{registryHost}/token?service={registryHost}&scope=repository:{repository}:pull,push");

            logger.LogDebug("{Repository} - fetching GHCR bearer token.", repository);

            using var request = new HttpRequestMessage(HttpMethod.Get, tokenUri);
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);

            using var response = http.Send(request);
            if (!response.IsSuccessStatusCode)
            {
                throw Fail(HttpMethod.Get, tokenUri, response);
            }

            using var doc = JsonDocument.Parse(response.Content.ReadAsStream());
            bearerToken = doc.RootElement.GetProperty("token").GetString();
            var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var exp) ? exp.GetInt32() : 60;
            tokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(expiresIn - 30, 10));

            return bearerToken;
        }
    }

    private byte[] BuildManifestBytes(string layerDigest, long layerSize, string cacheKey)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 2);
            writer.WriteString("mediaType", ManifestMediaType);

            writer.WriteStartObject("config");
            writer.WriteString("mediaType", EmptyConfigMediaType);
            writer.WriteString("digest", EmptyConfigDigest);
            writer.WriteNumber("size", EmptyConfigBytes.Length);
            writer.WriteEndObject();

            writer.WriteStartArray("layers");
            writer.WriteStartObject();
            writer.WriteString("mediaType", LayerMediaType);
            writer.WriteString("digest", layerDigest);
            writer.WriteNumber("size", layerSize);
            writer.WriteEndObject();
            writer.WriteEndArray();

            writer.WriteStartObject("annotations");
            writer.WriteString("dev.dzaba.build.cache-key", cacheKey);

            var repoSlug = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY");
            var serverUrl = Environment.GetEnvironmentVariable("GITHUB_SERVER_URL");
            if (!string.IsNullOrEmpty(repoSlug) && !string.IsNullOrEmpty(serverUrl))
            {
                writer.WriteString("org.opencontainers.image.source", $"{serverUrl}/{repoSlug}");
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private GhcrRegistryException Fail(HttpMethod method, Uri uri, HttpResponseMessage response)
    {
        string body;
        try
        {
            using var reader = new StreamReader(response.Content.ReadAsStream());
            var raw = reader.ReadToEnd();
            body = raw.Length > 512 ? raw[..512] : raw;
        }
        catch
        {
            body = "<unreadable>";
        }

        var message = $"GHCR {method} {uri} failed: {(int)response.StatusCode} {response.ReasonPhrase}. {body}";
        logger.LogWarning("{Message}", message);
        return new GhcrRegistryException(message);
    }

    private static string ToTag(string cacheKey)
    {
        if (string.IsNullOrEmpty(cacheKey))
        {
            throw new ArgumentException("Cache key must not be null or empty.", nameof(cacheKey));
        }

        if (IsTagSafe(cacheKey))
        {
            return "dzc-" + cacheKey;
        }

        return "dzcs-" + ComputeSha256Hex(Encoding.UTF8.GetBytes(cacheKey));
    }

    private static bool IsTagSafe(string value)
    {
        if (value.Length == 0 || value.Length > 123)
        {
            return false;
        }

        var first = value[0];
        if (!(char.IsAsciiLetterOrDigit(first) || first == '_'))
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    private static (string Host, string Repository) ParseImageReference(string imageReference)
    {
        if (string.IsNullOrWhiteSpace(imageReference))
        {
            throw new ArgumentException(
                "GhcrOciCacheStorage requires a non-empty image reference, e.g. 'ghcr.io/owner/repo/dzaba-cache'.",
                nameof(imageReference));
        }

        var trimmed = imageReference.Trim().TrimEnd('/');
        var segments = trimmed.Split('/');
        if (segments.Length < 3 || !(segments[0].Contains('.') || segments[0].Contains(':')))
        {
            throw new ArgumentException(
                $"Expected an image reference of the form '<registry-host>/<owner>/<name>[/...]', got '{imageReference}'.",
                nameof(imageReference));
        }

        var host = segments[0].ToLowerInvariant();
        var repository = string.Join('/', segments[1..]).ToLowerInvariant();
        return (host, repository);
    }

    private static string CreateDeterministicZip(string sourceDirectory)
    {
        var tempZip = Path.Combine(Path.GetTempPath(), "dzaba-ghcr-" + Guid.NewGuid().ToString("N") + ".zip");
        using (var archive = ZipFile.Open(tempZip, ZipArchiveMode.Create))
        {
            var files = Directory.EnumerateFiles(sourceDirectory)
                .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal);

            foreach (var file in files)
            {
                var entry = archive.CreateEntry(Path.GetFileName(file), CompressionLevel.Optimal);
                entry.LastWriteTime = ZipEntryTimestamp;
                using var entryStream = entry.Open();
                using var fileStream = File.OpenRead(file);
                fileStream.CopyTo(entryStream);
            }
        }

        return tempZip;
    }

    private static void ExtractFlat(string zipPath, string destinationDirectory)
    {
        var destinationFull = Path.GetFullPath(destinationDirectory);
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                // Directory entry - the flatness assumption means there should be none, but skip
                // rather than fail in case a producer ever includes one.
                continue;
            }

            if (entry.FullName.Contains('/') || entry.FullName.Contains('\\'))
            {
                throw new InvalidOperationException(
                    $"GHCR cache entry contains a nested path '{entry.FullName}'; Dzaba cache entries must be flat.");
            }

            var targetPath = Path.GetFullPath(Path.Combine(destinationFull, entry.Name));
            if (!targetPath.StartsWith(destinationFull + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"GHCR cache entry '{entry.FullName}' resolves outside the destination directory.");
            }

            entry.ExtractToFile(targetPath, overwrite: true);
        }
    }

    private static string ComputeSha256Hex(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return ToHex(SHA256.HashData(stream));
    }

    private static string ComputeSha256Hex(byte[] bytes)
    {
        return ToHex(SHA256.HashData(bytes));
    }

    private static string ToHex(byte[] bytes)
    {
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
