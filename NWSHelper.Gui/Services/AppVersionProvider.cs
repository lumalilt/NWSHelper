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
}
