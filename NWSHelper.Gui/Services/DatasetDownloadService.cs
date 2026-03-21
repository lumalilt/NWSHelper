using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NWSHelper.Core.Utils;

namespace NWSHelper.Gui.Services;

public sealed class DatasetDownloadService : IDatasetDownloadService, IDisposable
{
    private const string OpenAddressesProviderId = "openaddresses";
    private const string DefaultOpenAddressesApiBaseUrl = "https://batch.openaddresses.io/api";

    private static readonly DatasetProviderOption[] Providers =
    [
        new(OpenAddressesProviderId, "OpenAddresses", OpenAddressesProviderId)
    ];

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient httpClient;
    private readonly bool disposeHttpClient;

    public DatasetDownloadService(HttpClient? httpClient = null)
    {
        this.httpClient = httpClient ?? new HttpClient();
        disposeHttpClient = httpClient is null;
    }

    public IReadOnlyList<DatasetProviderOption> GetProviders() => Providers;

    public string ResolveProviderDatasetRoot(string datasetRootPath, string providerId)
    {
        var provider = Providers.FirstOrDefault(option => string.Equals(option.Id, providerId, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            throw new InvalidOperationException($"Unsupported dataset provider '{providerId}'.");
        }

        var effectiveRoot = string.IsNullOrWhiteSpace(datasetRootPath)
            ? "./datasets"
            : datasetRootPath;
        var fullRoot = Path.GetFullPath(effectiveRoot);

        var fullRootName = Path.GetFileName(fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.Equals(fullRootName, provider.RelativeRootFolder, StringComparison.OrdinalIgnoreCase))
        {
            return fullRoot;
        }

        return Path.Combine(fullRoot, provider.RelativeRootFolder);
    }

    public Task<IReadOnlyList<DatasetCatalogItem>> GetDatasetsAsync(
        string providerId,
        string? openAddressesApiBaseUrl,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(providerId, OpenAddressesProviderId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Unsupported dataset provider '{providerId}'.");
        }

        return GetOpenAddressesDatasetsAsync(openAddressesApiBaseUrl, cancellationToken);
    }

    public async Task<DatasetDownloadResult> DownloadDatasetsAsync(
        DatasetDownloadRequest request,
        IProgress<DatasetDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(request.ProviderId, OpenAddressesProviderId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Unsupported dataset provider '{request.ProviderId}'.");
        }

        var selectedKeys = request.SelectedDatasetKeys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (selectedKeys.Count == 0)
        {
            var providerRoot = ResolveProviderDatasetRoot(request.DatasetRootPath, request.ProviderId);
            return new DatasetDownloadResult(providerRoot, 0, 0);
        }

        var available = await GetOpenAddressesDatasetsAsync(request.OpenAddressesApiBaseUrl, cancellationToken);
        var lookup = new Dictionary<string, DatasetCatalogItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in available)
        {
            if (!lookup.TryGetValue(item.Key, out var existing) ||
                (item.JobId ?? 0) > (existing.JobId ?? 0))
            {
                lookup[item.Key] = item;
            }
        }

        var providerDatasetRoot = ResolveProviderDatasetRoot(request.DatasetRootPath, request.ProviderId);
        var usRoot = Path.Combine(providerDatasetRoot, "us");
        Directory.CreateDirectory(usRoot);

        var completed = 0;
        var requested = selectedKeys.Count;

        foreach (var key in selectedKeys)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!lookup.TryGetValue(key, out var item) || !item.JobId.HasValue)
            {
                continue;
            }

            progress?.Report(new DatasetDownloadProgress(completed, requested, key, $"Downloading {key}..."));

            var targetPath = ResolveOpenAddressesCsvPath(usRoot, key);
            var targetDirectory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(targetDirectory))
            {
                Directory.CreateDirectory(targetDirectory);
            }

            await DownloadOpenAddressesDatasetAsync(
                request.OpenAddressesApiBaseUrl,
                item.JobId.Value,
                targetPath,
                request.OpenAddressesApiToken,
                cancellationToken);

            completed++;
            progress?.Report(new DatasetDownloadProgress(completed, requested, key, $"Downloaded {key}."));
        }

        return new DatasetDownloadResult(providerDatasetRoot, completed, requested);
    }

    public async Task<DatasetProviderConnectionTestResult> TestProviderConnectionAsync(
        string providerId,
        string? openAddressesApiBaseUrl,
        string? openAddressesApiToken,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(providerId, OpenAddressesProviderId, StringComparison.OrdinalIgnoreCase))
        {
            return new DatasetProviderConnectionTestResult(false, $"Unsupported dataset provider '{providerId}'.");
        }

        try
        {
            var dataEndpoint = BuildApiUri(openAddressesApiBaseUrl, "/data?layer=addresses");
            using var pingRequest = new HttpRequestMessage(HttpMethod.Get, dataEndpoint);
            using var pingResponse = await httpClient.SendAsync(pingRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (!pingResponse.IsSuccessStatusCode)
            {
                return new DatasetProviderConnectionTestResult(
                    false,
                    $"Could not reach OpenAddresses API ({(int)pingResponse.StatusCode} {pingResponse.ReasonPhrase}).");
            }

            var normalizedToken = NormalizeBearerToken(openAddressesApiToken);
            if (string.IsNullOrWhiteSpace(normalizedToken))
            {
                return new DatasetProviderConnectionTestResult(
                    false,
                    "OpenAddresses API is reachable. Add an API token to verify authenticated downloads.");
            }

            var tokenEndpoint = BuildApiUri(openAddressesApiBaseUrl, "/token");
            using var tokenRequest = new HttpRequestMessage(HttpMethod.Get, tokenEndpoint);
            tokenRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", normalizedToken);

            using var tokenResponse = await httpClient.SendAsync(tokenRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (tokenResponse.IsSuccessStatusCode)
            {
                return new DatasetProviderConnectionTestResult(true, "OpenAddresses API token is valid.");
            }

            if (tokenResponse.StatusCode == HttpStatusCode.Forbidden || tokenResponse.StatusCode == HttpStatusCode.Unauthorized)
            {
                return new DatasetProviderConnectionTestResult(
                    false,
                    "OpenAddresses API is reachable, but the token was rejected (403/401). Verify token value and permissions.");
            }

            return new DatasetProviderConnectionTestResult(
                false,
                $"Token check returned {(int)tokenResponse.StatusCode} {tokenResponse.ReasonPhrase}.");
        }
        catch (Exception ex)
        {
            return new DatasetProviderConnectionTestResult(false, ex.Message);
        }
    }

    public void Dispose()
    {
        if (disposeHttpClient)
        {
            httpClient.Dispose();
        }
    }

    private async Task<IReadOnlyList<DatasetCatalogItem>> GetOpenAddressesDatasetsAsync(
        string? openAddressesApiBaseUrl,
        CancellationToken cancellationToken)
    {
        var endpoint = BuildApiUri(openAddressesApiBaseUrl, "/data?layer=addresses");

        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync<List<OpenAddressesDataRecord>>(stream, SerializerOptions, cancellationToken)
            ?? [];

        var items = payload
            .Where(record => !string.IsNullOrWhiteSpace(record.Source) && record.Job > 0)
            .GroupBy(record => record.Source!, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(item => item.Updated ?? 0)
                .ThenByDescending(item => item.Job)
                .First())
            .Select(record => new DatasetCatalogItem(
                OpenAddressesProviderId,
                record.Source!,
                record.Source!,
                record.Job,
                record.Size))
            .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return items;
    }

    private async Task DownloadOpenAddressesDatasetAsync(
        string? openAddressesApiBaseUrl,
        int jobId,
        string targetCsvPath,
        string? openAddressesApiToken,
        CancellationToken cancellationToken)
    {
        var endpoint = BuildApiUri(openAddressesApiBaseUrl, $"/job/{jobId}/output/source.geojson.gz");

        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        var normalizedToken = NormalizeBearerToken(openAddressesApiToken);
        if (!string.IsNullOrWhiteSpace(normalizedToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", normalizedToken);
        }

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException("OpenAddresses download request was forbidden. Set an API token in Settings (OpenAddresses API token).");
        }

        response.EnsureSuccessStatusCode();

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await WriteOpenAddressesCsvAsync(responseStream, targetCsvPath, cancellationToken);
    }

    private static async Task WriteOpenAddressesCsvAsync(Stream sourceGzipStream, string targetCsvPath, CancellationToken cancellationToken)
    {
        await using var file = File.Open(targetCsvPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await using var writer = new StreamWriter(file);
        using var csv = CsvFactory.CreateWriter(writer);

        csv.WriteField("number");
        csv.WriteField("house_number");
        csv.WriteField("street");
        csv.WriteField("unit");
        csv.WriteField("city");
        csv.WriteField("region");
        csv.WriteField("postcode");
        csv.WriteField("lat");
        csv.WriteField("lon");
        csv.WriteField("name");
        csv.WriteField("phone");
        csv.WriteField("type");
        csv.NextRecord();

        var tempGeoJsonPath = Path.Combine(Path.GetTempPath(), $"teritaddy-oa-{Guid.NewGuid():N}.json");
        try
        {
            await using (var tempFile = File.Open(tempGeoJsonPath, FileMode.Create, FileAccess.Write, FileShare.None))
            await using (var gzipStream = new GZipStream(sourceGzipStream, CompressionMode.Decompress, leaveOpen: false))
            {
                await gzipStream.CopyToAsync(tempFile, cancellationToken);
            }

            await WriteCsvFromGeoJsonFileAsync(tempGeoJsonPath, csv, cancellationToken);
        }
        finally
        {
            try
            {
                if (File.Exists(tempGeoJsonPath))
                {
                    File.Delete(tempGeoJsonPath);
                }
            }
            catch
            {
            }
        }

        await writer.FlushAsync(cancellationToken);
    }

    private static async Task WriteCsvFromGeoJsonFileAsync(string geoJsonFilePath, CsvHelper.CsvWriter csv, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(geoJsonFilePath);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("features", out var features) &&
                features.ValueKind == JsonValueKind.Array)
            {
                foreach (var feature in features.EnumerateArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    WriteCsvFeatureRecord(csv, feature);
                }

                return;
            }

            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var feature in root.EnumerateArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (feature.ValueKind == JsonValueKind.Object)
                    {
                        WriteCsvFeatureRecord(csv, feature);
                    }
                }

                return;
            }

            if (root.ValueKind == JsonValueKind.Object)
            {
                WriteCsvFeatureRecord(csv, root);
            }
        }
        catch (JsonException)
        {
            await WriteCsvFromJsonLinesAsync(geoJsonFilePath, csv, cancellationToken);
        }
    }

    private static async Task WriteCsvFromJsonLinesAsync(string geoJsonFilePath, CsvHelper.CsvWriter csv, CancellationToken cancellationToken)
    {
        using var stream = File.OpenRead(geoJsonFilePath);
        using var reader = new StreamReader(stream);

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;

                if (root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("features", out var features) &&
                    features.ValueKind == JsonValueKind.Array)
                {
                    foreach (var feature in features.EnumerateArray())
                    {
                        WriteCsvFeatureRecord(csv, feature);
                    }

                    continue;
                }

                if (root.ValueKind == JsonValueKind.Object)
                {
                    WriteCsvFeatureRecord(csv, root);
                }
            }
            catch (JsonException)
            {
            }
        }
    }

    private static void WriteCsvFeatureRecord(CsvHelper.CsvWriter csv, JsonElement feature)
    {
        var properties = feature.TryGetProperty("properties", out var propertiesElement) && propertiesElement.ValueKind == JsonValueKind.Object
            ? propertiesElement
            : default;

        var number = GetPropertyValue(properties, "number", "house_number", "addr:housenumber");
        var street = GetPropertyValue(properties, "street", "addr:street");
        var unit = GetPropertyValue(properties, "unit", "addr:unit", "addr:suite");
        var city = GetPropertyValue(properties, "city", "locality");
        var region = GetPropertyValue(properties, "region", "state");
        var postcode = GetPropertyValue(properties, "postcode", "zip", "postal_code");
        var name = GetPropertyValue(properties, "name");
        var phone = GetPropertyValue(properties, "phone");
        var type = GetPropertyValue(properties, "type");

        var (lon, lat) = TryReadGeometry(feature);

        csv.WriteField(number);
        csv.WriteField(number);
        csv.WriteField(street);
        csv.WriteField(unit);
        csv.WriteField(city);
        csv.WriteField(region);
        csv.WriteField(postcode);
        csv.WriteField(lat.HasValue ? lat.Value.ToString(CultureInfo.InvariantCulture) : null);
        csv.WriteField(lon.HasValue ? lon.Value.ToString(CultureInfo.InvariantCulture) : null);
        csv.WriteField(name);
        csv.WriteField(phone);
        csv.WriteField(string.IsNullOrWhiteSpace(type) ? "House" : type);
        csv.NextRecord();
    }

    private static (double? Lon, double? Lat) TryReadGeometry(JsonElement feature)
    {
        if (!feature.TryGetProperty("geometry", out var geometry) || geometry.ValueKind != JsonValueKind.Object)
        {
            return (null, null);
        }

        if (!geometry.TryGetProperty("coordinates", out var coordinates) || coordinates.ValueKind != JsonValueKind.Array)
        {
            return (null, null);
        }

        if (coordinates.GetArrayLength() < 2)
        {
            return (null, null);
        }

        var lon = TryReadDouble(coordinates[0]);
        var lat = TryReadDouble(coordinates[1]);
        return (lon, lat);
    }

    private static double? TryReadDouble(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var value))
        {
            return value;
        }

        if (element.ValueKind == JsonValueKind.String &&
            double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static string? GetPropertyValue(JsonElement properties, params string[] names)
    {
        if (properties.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in names)
        {
            foreach (var property in properties.EnumerateObject())
            {
                if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Number => property.Value.GetRawText(),
                    JsonValueKind.True => bool.TrueString,
                    JsonValueKind.False => bool.FalseString,
                    _ => property.Value.ToString()
                };
            }
        }

        return null;
    }

    private static string ResolveOpenAddressesCsvPath(string usRoot, string datasetKey)
    {
        var segments = datasetKey
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(SanitizePathSegment)
            .ToArray();

        if (segments.Length >= 3 && string.Equals(segments[0], "us", StringComparison.OrdinalIgnoreCase))
        {
            var state = segments[1].ToLowerInvariant();
            var county = string.Join('_', segments.Skip(2)).ToLowerInvariant();
            return Path.Combine(usRoot, state, $"{county}.csv");
        }

        var fallback = string.Join('_', segments).ToLowerInvariant();
        return Path.Combine(usRoot, "misc", $"{fallback}.csv");
    }

    private static string SanitizePathSegment(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var normalized = value.Trim();
        foreach (var invalidChar in invalidChars)
        {
            normalized = normalized.Replace(invalidChar, '_');
        }

        return string.IsNullOrWhiteSpace(normalized) ? "unknown" : normalized;
    }

    private static Uri BuildApiUri(string? baseUrl, string relative)
    {
        var effectiveBase = string.IsNullOrWhiteSpace(baseUrl)
            ? DefaultOpenAddressesApiBaseUrl
            : baseUrl.Trim();

        if (!effectiveBase.EndsWith("/", StringComparison.Ordinal))
        {
            effectiveBase += "/";
        }

        var relativePath = relative.TrimStart('/');
        return new Uri(new Uri(effectiveBase, UriKind.Absolute), relativePath);
    }

    private static string? NormalizeBearerToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var trimmed = token.Trim();
        const string bearerPrefix = "Bearer ";
        if (trimmed.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[bearerPrefix.Length..].Trim();
        }

        return string.IsNullOrWhiteSpace(trimmed)
            ? null
            : trimmed;
    }

    private sealed class OpenAddressesDataRecord
    {
        public string? Source { get; set; }

        public int Job { get; set; }

        public long? Updated { get; set; }

        public long? Size { get; set; }
    }
}

