using System;
using System.Collections.Generic;
using System.Reflection;

namespace NWSHelper.Gui.Services;

public sealed class StoreListingOptions
{
    private const string DefaultSearchQuery = "NWS Helper";
    internal const string StoreAppProductIdMetadataKey = "NWSHelperStoreAppProductId";

    public string AppProductId { get; init; } = ReadEnvironmentVariable("NWSHELPER_STORE_APP_PRODUCT_ID");

    public string SearchQuery { get; init; } = ReadEnvironmentVariable("NWSHELPER_STORE_APP_SEARCH_QUERY", DefaultSearchQuery);

    public string WebUrl { get; init; } = ReadEnvironmentVariable("NWSHELPER_STORE_APP_WEB_URL");

    public string ResolvedAppProductId => ResolveAppProductId();

    public IReadOnlyList<string> GetLaunchUrls()
    {
        var storeAppUrl = BuildStoreAppUrl();
        var webUrl = BuildWebUrl();

        if (!OperatingSystem.IsWindows())
        {
            return [webUrl];
        }

        return string.Equals(storeAppUrl, webUrl, StringComparison.OrdinalIgnoreCase)
            ? [storeAppUrl]
            : [storeAppUrl, webUrl];
    }

    public string BuildStoreAppUrl()
    {
        var productId = ResolveAppProductId();
        return !string.IsNullOrWhiteSpace(productId)
            ? $"ms-windows-store://pdp/?ProductId={Uri.EscapeDataString(productId)}"
            : $"ms-windows-store://search/?query={Uri.EscapeDataString(SearchQuery)}";
    }

    public string BuildWebUrl()
    {
        var productId = ResolveAppProductId();

        if (!string.IsNullOrWhiteSpace(WebUrl))
        {
            return WebUrl;
        }

        return !string.IsNullOrWhiteSpace(productId)
            ? $"https://apps.microsoft.com/detail/{Uri.EscapeDataString(productId)}"
            : $"https://apps.microsoft.com/search?query={Uri.EscapeDataString(SearchQuery)}";
    }

    private string ResolveAppProductId()
    {
        if (!string.IsNullOrWhiteSpace(AppProductId))
        {
            return AppProductId;
        }

        if (string.IsNullOrWhiteSpace(WebUrl) || !Uri.TryCreate(WebUrl, UriKind.Absolute, out var uri))
        {
            return ReadAssemblyMetadataValue(StoreAppProductIdMetadataKey);
        }

        if (!uri.Host.Contains("apps.microsoft.com", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 || !string.Equals(segments[0], "detail", StringComparison.OrdinalIgnoreCase))
        {
            return ReadAssemblyMetadataValue(StoreAppProductIdMetadataKey);
        }

        return segments[1].Trim();
    }

    private static string ReadEnvironmentVariable(string name, string defaultValue = "")
    {
        var value = (Environment.GetEnvironmentVariable(name) ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(value) ? defaultValue : value;
    }

    private static string ReadAssemblyMetadataValue(string key)
    {
        foreach (var assembly in new[] { Assembly.GetEntryAssembly(), Assembly.GetExecutingAssembly() })
        {
            if (assembly is null)
            {
                continue;
            }

            foreach (var attribute in assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
            {
                if (string.Equals(attribute.Key, key, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(attribute.Value))
                {
                    return attribute.Value.Trim();
                }
            }
        }

        return string.Empty;
    }
}