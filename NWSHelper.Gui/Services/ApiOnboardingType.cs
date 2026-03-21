using System;
using System.Collections.Generic;

namespace NWSHelper.Gui.Services;

public enum ApiOnboardingType
{
    Automated,
    EmbeddedManual,
    FullyManual,
    Debugging
}

public static class ApiOnboardingTypeLabels
{
    public const string Automated = "Automated";
    public const string EmbeddedManual = "Embedded Manual";
    public const string FullyManual = "Fully Manual";
    public const string Debugging = "Debugging";

    public static IReadOnlyList<string> All { get; } =
    [
        Automated,
        EmbeddedManual,
        FullyManual,
        Debugging
    ];

    public static string ToLabel(ApiOnboardingType onboardingType)
    {
        return onboardingType switch
        {
            ApiOnboardingType.Automated => Automated,
            ApiOnboardingType.EmbeddedManual => EmbeddedManual,
            ApiOnboardingType.FullyManual => FullyManual,
            ApiOnboardingType.Debugging => Debugging,
            _ => Automated
        };
    }

    public static bool TryParse(string? value, out ApiOnboardingType onboardingType)
    {
        var normalized = (value ?? string.Empty).Trim().Replace(" ", string.Empty, StringComparison.OrdinalIgnoreCase);
        normalized = normalized.Replace("-", string.Empty, StringComparison.OrdinalIgnoreCase);
        normalized = normalized.ToLowerInvariant();

        onboardingType = normalized switch
        {
            "automated" => ApiOnboardingType.Automated,
            "manual" => ApiOnboardingType.EmbeddedManual,
            "embeddedmanual" => ApiOnboardingType.EmbeddedManual,
            "fullymanual" => ApiOnboardingType.FullyManual,
            "debugging" => ApiOnboardingType.Debugging,
            _ => ApiOnboardingType.Automated
        };

        return normalized is "automated" or "manual" or "embeddedmanual" or "fullymanual" or "debugging";
    }

    public static ApiOnboardingType ParseOrDefault(string? value, ApiOnboardingType fallback = ApiOnboardingType.Automated)
    {
        return TryParse(value, out var parsed) ? parsed : fallback;
    }
}
