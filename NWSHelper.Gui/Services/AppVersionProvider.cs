using System;
using System.Reflection;

namespace NWSHelper.Gui.Services;

public static class AppVersionProvider
{
    public static string GetDisplayVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            return informational;
        }

        var version = assembly.GetName().Version;
        return version is null ? "0.0.0" : version.ToString();
    }

    public static string GetUpdateComparisonVersion()
    {
        return NormalizeForUpdateComparison(GetDisplayVersion());
    }

    public static string NormalizeForUpdateComparison(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return "0.0.0";
        }

        var trimmed = version.Trim();
        var buildMetadataSeparatorIndex = trimmed.IndexOf('+', StringComparison.Ordinal);
        if (buildMetadataSeparatorIndex <= 0)
        {
            return trimmed;
        }

        return trimmed[..buildMetadataSeparatorIndex];
    }
}
