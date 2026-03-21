using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Microsoft.Win32;
using NWSHelper.Gui.Views;

namespace NWSHelper.Gui.Services;

public enum EmbeddedOnboardingDependencyState
{
    Available,
    Unavailable,
    Unknown
}

public readonly record struct EmbeddedOnboardingDependencyResult(
    EmbeddedOnboardingDependencyState State,
    string Message)
{
    public bool ShouldUseExternalFallback => State == EmbeddedOnboardingDependencyState.Unavailable;
}

public interface IEmbeddedOnboardingDependencyChecker
{
    EmbeddedOnboardingDependencyResult CheckEmbeddedDependency();
}

public sealed class WebView2EmbeddedOnboardingDependencyChecker : IEmbeddedOnboardingDependencyChecker
{
    private const string WebView2RuntimeClientId = "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
    private static readonly string[] WebView2RuntimeRegistryPaths =
    [
        $@"SOFTWARE\Microsoft\EdgeUpdate\Clients\{WebView2RuntimeClientId}",
        $@"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{WebView2RuntimeClientId}"
    ];

    public EmbeddedOnboardingDependencyResult CheckEmbeddedDependency()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new EmbeddedOnboardingDependencyResult(
                EmbeddedOnboardingDependencyState.Unknown,
                "WebView2 runtime probing is Windows-specific.");
        }

        try
        {
            if (TryGetInstalledWebView2Version(out var version))
            {
                return new EmbeddedOnboardingDependencyResult(
                    EmbeddedOnboardingDependencyState.Available,
                    $"WebView2 runtime detected ({version}).");
            }

            return new EmbeddedOnboardingDependencyResult(
                EmbeddedOnboardingDependencyState.Unavailable,
                "Microsoft Edge WebView2 Runtime was not detected on this system.");
        }
        catch (Exception ex)
        {
            return new EmbeddedOnboardingDependencyResult(
                EmbeddedOnboardingDependencyState.Unknown,
                $"WebView2 runtime probe failed: {ex.Message}");
        }
    }

    private static bool TryGetInstalledWebView2Version(out string version)
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                if (TryReadVersionFromHive(hive, view, out version))
                {
                    return true;
                }
            }
        }

        version = string.Empty;
        return false;
    }

    private static bool TryReadVersionFromHive(RegistryHive hive, RegistryView view, out string version)
    {
        version = string.Empty;

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            foreach (var keyPath in WebView2RuntimeRegistryPaths)
            {
                using var key = baseKey.OpenSubKey(keyPath, writable: false);
                if (key?.GetValue("pv") is not string rawVersion)
                {
                    continue;
                }

                var normalized = rawVersion.Trim();
                if (string.IsNullOrWhiteSpace(normalized) ||
                    string.Equals(normalized, "0.0.0.0", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                version = normalized;
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }
}

public interface IOpenAddressesOnboardingDialogSession
{
    string LastSuccessfulApiTokenCaptureSummary { get; }

    Task<string?> ShowAsync();
}

public interface IOpenAddressesOnboardingDialogSessionFactory
{
    IOpenAddressesOnboardingDialogSession Create(bool useEmbeddedOnboardingBrowser, ApiOnboardingType onboardingType);
}

public readonly record struct OpenAddressesOnboardingLaunchResult(
    string ApiToken,
    string LastSuccessfulApiTokenCaptureSummary,
    bool UsedExternalFallback,
    bool PersistExternalFallbackPreference,
    string StatusMessage)
{
    public bool HasApiToken => !string.IsNullOrWhiteSpace(ApiToken);
}

public sealed class OpenAddressesOnboardingCoordinator
{
    private readonly IEmbeddedOnboardingDependencyChecker dependencyChecker;

    public OpenAddressesOnboardingCoordinator(IEmbeddedOnboardingDependencyChecker? dependencyChecker = null)
    {
        this.dependencyChecker = dependencyChecker ?? new WebView2EmbeddedOnboardingDependencyChecker();
    }

    public async Task<OpenAddressesOnboardingLaunchResult> LaunchAsync(
        IOpenAddressesOnboardingDialogSessionFactory dialogFactory,
        bool useEmbeddedOnboardingBrowser,
        ApiOnboardingType onboardingType)
    {
        ArgumentNullException.ThrowIfNull(dialogFactory);

        var shouldAttemptEmbedded = ShouldAttemptEmbeddedOnboarding(useEmbeddedOnboardingBrowser, onboardingType);
        if (shouldAttemptEmbedded)
        {
            var dependency = dependencyChecker.CheckEmbeddedDependency();
            if (dependency.ShouldUseExternalFallback)
            {
                var reason = $"Embedded onboarding dependency unavailable ({dependency.Message})";
                return await LaunchExternalFallbackAsync(dialogFactory, reason);
            }
        }

        try
        {
            var session = dialogFactory.Create(useEmbeddedOnboardingBrowser, onboardingType);
            return await LaunchSessionAsync(
                session,
                usedExternalFallback: false,
                persistExternalFallbackPreference: false,
                statusMessage: string.Empty);
        }
        catch (Exception ex) when (shouldAttemptEmbedded)
        {
            var reason = $"Embedded onboarding failed to initialize ({ex.Message})";
            return await LaunchExternalFallbackAsync(dialogFactory, reason);
        }
        catch (Exception ex)
        {
            return new OpenAddressesOnboardingLaunchResult(
                ApiToken: string.Empty,
                LastSuccessfulApiTokenCaptureSummary: string.Empty,
                UsedExternalFallback: false,
                PersistExternalFallbackPreference: false,
                StatusMessage: $"OpenAddresses onboarding failed to start ({ex.Message}).");
        }
    }

    internal static bool ShouldAttemptEmbeddedOnboarding(bool useEmbeddedOnboardingBrowser, ApiOnboardingType onboardingType)
    {
        return onboardingType switch
        {
            ApiOnboardingType.FullyManual => false,
            ApiOnboardingType.EmbeddedManual => true,
            ApiOnboardingType.Debugging => true,
            _ => useEmbeddedOnboardingBrowser
        };
    }

    private static async Task<OpenAddressesOnboardingLaunchResult> LaunchExternalFallbackAsync(
        IOpenAddressesOnboardingDialogSessionFactory dialogFactory,
        string reason)
    {
        var statusMessage = $"{reason}. Falling back to external browser onboarding.";

        try
        {
            var fallbackSession = dialogFactory.Create(
                useEmbeddedOnboardingBrowser: false,
                onboardingType: ApiOnboardingType.FullyManual);

            return await LaunchSessionAsync(
                fallbackSession,
                usedExternalFallback: true,
                persistExternalFallbackPreference: true,
                statusMessage: statusMessage);
        }
        catch (Exception ex)
        {
            return new OpenAddressesOnboardingLaunchResult(
                ApiToken: string.Empty,
                LastSuccessfulApiTokenCaptureSummary: string.Empty,
                UsedExternalFallback: true,
                PersistExternalFallbackPreference: true,
                StatusMessage: $"{statusMessage} External onboarding launch failed ({ex.Message}).");
        }
    }

    private static async Task<OpenAddressesOnboardingLaunchResult> LaunchSessionAsync(
        IOpenAddressesOnboardingDialogSession session,
        bool usedExternalFallback,
        bool persistExternalFallbackPreference,
        string statusMessage)
    {
        var token = (await session.ShowAsync())?.Trim() ?? string.Empty;
        return new OpenAddressesOnboardingLaunchResult(
            ApiToken: token,
            LastSuccessfulApiTokenCaptureSummary: session.LastSuccessfulApiTokenCaptureSummary ?? string.Empty,
            UsedExternalFallback: usedExternalFallback,
            PersistExternalFallbackPreference: persistExternalFallbackPreference,
            StatusMessage: statusMessage);
    }
}

internal sealed class OpenAddressesOnboardingWindowDialogSessionFactory : IOpenAddressesOnboardingDialogSessionFactory
{
    private readonly Window ownerWindow;

    public OpenAddressesOnboardingWindowDialogSessionFactory(Window ownerWindow)
    {
        this.ownerWindow = ownerWindow;
    }

    public IOpenAddressesOnboardingDialogSession Create(bool useEmbeddedOnboardingBrowser, ApiOnboardingType onboardingType)
    {
        var window = new OpenAddressesOnboardingWindow(useEmbeddedOnboardingBrowser, onboardingType);
        return new OpenAddressesOnboardingWindowDialogSession(ownerWindow, window);
    }
}

internal sealed class OpenAddressesOnboardingWindowDialogSession : IOpenAddressesOnboardingDialogSession
{
    private readonly Window ownerWindow;
    private readonly OpenAddressesOnboardingWindow onboardingWindow;

    public OpenAddressesOnboardingWindowDialogSession(Window ownerWindow, OpenAddressesOnboardingWindow onboardingWindow)
    {
        this.ownerWindow = ownerWindow;
        this.onboardingWindow = onboardingWindow;
    }

    public string LastSuccessfulApiTokenCaptureSummary => onboardingWindow.LastSuccessfulApiTokenCaptureSummary;

    public Task<string?> ShowAsync()
    {
        return onboardingWindow.ShowDialog<string?>(ownerWindow);
    }
}