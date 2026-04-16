using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NetSparkleUpdater;
using NetSparkleUpdater.Enums;
using NetSparkleUpdater.SignatureVerifiers;
using NetSparkleUpdater.UI.Avalonia;

namespace NWSHelper.Gui.Services;

public sealed class GuiUpdateSettings
{
    public bool AutoUpdateEnabled { get; set; } = true;

    public string AppcastUrl { get; set; } = Environment.GetEnvironmentVariable("NWSHELPER_APPCAST_URL") ?? string.Empty;

    public string AppcastPublicKey { get; set; } = Environment.GetEnvironmentVariable("NWSHELPER_APPCAST_PUBLIC_KEY") ?? string.Empty;

    public DateTimeOffset? LastCheckedUtc { get; set; }

    public string LastCheckStatus { get; set; } = string.Empty;
}

public sealed class UpdateCheckResult
{
    public bool IsUpdateAvailable { get; init; }

    public bool IsInstallerReady { get; init; }

    public bool WasCheckSuccessful { get; init; }

    public string Message { get; init; } = string.Empty;

    public string? LatestVersion { get; init; }

    public bool UsedStorePath { get; init; }
}

public sealed class UpdateInstallResult
{
    public bool StartedInstaller { get; init; }

    public string Message { get; init; } = string.Empty;

    public string? LatestVersion { get; init; }

    public bool UsedCachedInstaller { get; init; }
}

public interface IUpdateService
{
    string CurrentVersion { get; }

    string VersionOverrideForTesting { get; set; }

    bool IsStoreInstall { get; }

    bool AutoUpdateEnabled { get; set; }

    Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken);

    Task<UpdateCheckResult> CheckForUpdatesOnStartupAsync(CancellationToken cancellationToken);

    Task<UpdateInstallResult> DownloadAndInstallPendingUpdateAsync(CancellationToken cancellationToken);
}

public sealed class NetSparkleUpdateService : IUpdateService, IDisposable
{
    private const string AppcastUrlEnvironmentVariable = "NWSHELPER_APPCAST_URL";
    private const string AppcastPublicKeyEnvironmentVariable = "NWSHELPER_APPCAST_PUBLIC_KEY";
    private const string LegacyPrivateGitHubOwner = "dmealo";
    private const string PublicGitHubOwner = "lumalilt";
    private const string RepositoryName = "NWSHelper";
    private static readonly Uri UpdateIconAssetUri = new("avares://NWSHelper.Gui/Assets/nwsh_multi.ico");
    private static readonly TimeSpan StartupCheckFrequency = TimeSpan.FromHours(12);
    private static readonly PropertyInfo? SparkleUpdaterConfigurationProperty = typeof(SparkleUpdater).GetProperty("Configuration", BindingFlags.Instance | BindingFlags.Public);
    private static readonly PropertyInfo? SparkleUpdaterInstalledVersionProperty = SparkleUpdaterConfigurationProperty?.PropertyType.GetProperty("InstalledVersion", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private readonly GuiConfigurationStore configurationStore;
    private readonly GuiUpdateSettings updateSettings;
    private readonly IStoreRuntimeContextProvider storeRuntimeContextProvider;
    private readonly object updaterSync = new();
    private readonly object pendingUpdateSync = new();
    private readonly string actualCurrentVersion;

    private SparkleUpdater? sparkleUpdater;
    private string? sparkleUpdaterAppcastUrl;
    private string? sparkleUpdaterAppcastPublicKey;
    private AppCastItem? pendingUpdateItem;
    private string installedComparisonVersion;
    private string versionOverrideForTesting = string.Empty;
    private bool updateLoopStarted;
    private bool disposed;

    public NetSparkleUpdateService(string? filePath = null, IStoreRuntimeContextProvider? storeRuntimeContextProvider = null, string? currentVersionOverride = null)
    {
        configurationStore = new GuiConfigurationStore(filePath);
        updateSettings = LoadSettings();
        this.storeRuntimeContextProvider = storeRuntimeContextProvider ?? new StoreRuntimeContextProvider(filePath: filePath);
        actualCurrentVersion = string.IsNullOrWhiteSpace(currentVersionOverride)
            ? AppVersionProvider.GetDisplayVersion()
            : currentVersionOverride.Trim();
        installedComparisonVersion = AppVersionProvider.NormalizeForUpdateComparison(actualCurrentVersion);
    }

    public string CurrentVersion => actualCurrentVersion;

    public string VersionOverrideForTesting
    {
        get => versionOverrideForTesting;
        set
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (string.Equals(versionOverrideForTesting, normalized, StringComparison.Ordinal))
            {
                return;
            }

            versionOverrideForTesting = normalized;
            installedComparisonVersion = ResolveInstalledComparisonVersion();

            lock (updaterSync)
            {
                if (sparkleUpdater is not null)
                {
                    ApplyInstalledVersionOverride(sparkleUpdater, installedComparisonVersion);
                }
            }
        }
    }

    public bool IsStoreInstall => storeRuntimeContextProvider.GetCurrent().IsStoreInstall;

    public bool AutoUpdateEnabled
    {
        get => updateSettings.AutoUpdateEnabled;
        set
        {
            if (updateSettings.AutoUpdateEnabled == value)
            {
                return;
            }

            updateSettings.AutoUpdateEnabled = value;
            SaveSettings("Auto-update preference changed.");
        }
    }

    public Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken)
    {
        return CheckForUpdatesInternalAsync(fromStartupPolicy: false, cancellationToken);
    }

    public Task<UpdateCheckResult> CheckForUpdatesOnStartupAsync(CancellationToken cancellationToken)
    {
        return CheckForUpdatesInternalAsync(fromStartupPolicy: true, cancellationToken);
    }

    public async Task<UpdateInstallResult> DownloadAndInstallPendingUpdateAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        if (IsStoreInstall)
        {
            return new UpdateInstallResult
            {
                StartedInstaller = false,
                Message = "Store install detected. Use Microsoft Store servicing for updates.",
                UsedCachedInstaller = false
            };
        }

        var pendingUpdate = GetPendingUpdate();
        if (pendingUpdate is null)
        {
            return new UpdateInstallResult
            {
                StartedInstaller = false,
                Message = "No pending update is available to install.",
                UsedCachedInstaller = false
            };
        }

        var appcastUrl = ResolveAppcastUrl();
        if (string.IsNullOrWhiteSpace(appcastUrl))
        {
            return new UpdateInstallResult
            {
                StartedInstaller = false,
                Message = $"No appcast URL configured. Set {AppcastUrlEnvironmentVariable} for NetSparkle checks.",
                LatestVersion = GetVersionLabel(pendingUpdate),
                UsedCachedInstaller = false
            };
        }

        var appcastPublicKey = ResolveAppcastPublicKey();
        if (string.IsNullOrWhiteSpace(appcastPublicKey))
        {
            return new UpdateInstallResult
            {
                StartedInstaller = false,
                Message = $"No appcast public key configured. Set {AppcastPublicKeyEnvironmentVariable} for strict NetSparkle verification.",
                LatestVersion = GetVersionLabel(pendingUpdate),
                UsedCachedInstaller = false
            };
        }

        var updater = GetOrCreateSparkleUpdater(appcastUrl, appcastPublicKey);
        var latestVersion = GetVersionLabel(pendingUpdate);
        var cachedInstallerPath = await TryGetDownloadedInstallerPathAsync(updater, pendingUpdate).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(cachedInstallerPath))
        {
            await InstallUpdateAsync(updater, pendingUpdate, cachedInstallerPath).ConfigureAwait(false);
            return new UpdateInstallResult
            {
                StartedInstaller = true,
                Message = BuildInstallStartedMessage(latestVersion, usedCachedInstaller: true),
                LatestVersion = latestVersion,
                UsedCachedInstaller = true
            };
        }

        var completionSource = new TaskCompletionSource<UpdateInstallResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        async void HandleDownloadFinished(AppCastItem item, string? path)
        {
            if (!ReferenceEquals(item, pendingUpdate))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                completionSource.TrySetResult(new UpdateInstallResult
                {
                    StartedInstaller = false,
                    Message = "Update download completed without a usable installer path.",
                    LatestVersion = latestVersion,
                    UsedCachedInstaller = false
                });
                return;
            }

            try
            {
                await InstallUpdateAsync(updater, pendingUpdate, path).ConfigureAwait(false);
                completionSource.TrySetResult(new UpdateInstallResult
                {
                    StartedInstaller = true,
                    Message = BuildInstallStartedMessage(latestVersion, usedCachedInstaller: false),
                    LatestVersion = latestVersion,
                    UsedCachedInstaller = false
                });
            }
            catch (Exception ex)
            {
                completionSource.TrySetResult(new UpdateInstallResult
                {
                    StartedInstaller = false,
                    Message = $"Update download completed but installer launch failed: {ex.Message}",
                    LatestVersion = latestVersion,
                    UsedCachedInstaller = false
                });
            }
        }

        void HandleDownloadCanceled(AppCastItem item, string? path)
        {
            if (!ReferenceEquals(item, pendingUpdate))
            {
                return;
            }

            completionSource.TrySetResult(new UpdateInstallResult
            {
                StartedInstaller = false,
                Message = "Update download was canceled.",
                LatestVersion = latestVersion,
                UsedCachedInstaller = false
            });
        }

        void HandleDownloadError(AppCastItem item, string? path, Exception exception)
        {
            if (!ReferenceEquals(item, pendingUpdate))
            {
                return;
            }

            completionSource.TrySetResult(new UpdateInstallResult
            {
                StartedInstaller = false,
                Message = $"Update download failed: {exception.Message}",
                LatestVersion = latestVersion,
                UsedCachedInstaller = false
            });
        }

        void HandleDownloadedFileIsCorrupt(AppCastItem item, string? path)
        {
            if (!ReferenceEquals(item, pendingUpdate))
            {
                return;
            }

            completionSource.TrySetResult(new UpdateInstallResult
            {
                StartedInstaller = false,
                Message = "Downloaded update failed signature verification.",
                LatestVersion = latestVersion,
                UsedCachedInstaller = false
            });
        }

        updater.DownloadFinished += HandleDownloadFinished;
        updater.DownloadCanceled += HandleDownloadCanceled;
        updater.DownloadHadError += HandleDownloadError;
        updater.DownloadedFileIsCorrupt += HandleDownloadedFileIsCorrupt;

        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            try
            {
                updater.CancelFileDownload();
            }
            catch
            {
            }

            completionSource.TrySetCanceled(cancellationToken);
        });

        try
        {
            await updater.InitAndBeginDownload(pendingUpdate).ConfigureAwait(false);
            return await completionSource.Task.ConfigureAwait(false);
        }
        finally
        {
            updater.DownloadFinished -= HandleDownloadFinished;
            updater.DownloadCanceled -= HandleDownloadCanceled;
            updater.DownloadHadError -= HandleDownloadError;
            updater.DownloadedFileIsCorrupt -= HandleDownloadedFileIsCorrupt;
        }
    }

    private async Task<UpdateCheckResult> CheckForUpdatesInternalAsync(bool fromStartupPolicy, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        if (IsStoreInstall)
        {
            var message = "Store install detected. Use Microsoft Store servicing APIs for update checks.";
            SaveSettings(message);
            ClearPendingUpdate();
            return new UpdateCheckResult
            {
                IsUpdateAvailable = false,
                IsInstallerReady = false,
                Message = message,
                WasCheckSuccessful = true,
                UsedStorePath = true
            };
        }

        if (fromStartupPolicy && !AutoUpdateEnabled)
        {
            var disabledMessage = "Auto-update checks are disabled in Settings.";
            SaveSettings(disabledMessage);
            ClearPendingUpdate();
            return new UpdateCheckResult
            {
                IsUpdateAvailable = false,
                IsInstallerReady = false,
                Message = disabledMessage,
                WasCheckSuccessful = true,
                UsedStorePath = false
            };
        }

        var appcastUrl = ResolveAppcastUrl();
        if (string.IsNullOrWhiteSpace(appcastUrl))
        {
            var missingMessage = $"No appcast URL configured. Set {AppcastUrlEnvironmentVariable} for NetSparkle checks.";
            SaveSettings(missingMessage);
            ClearPendingUpdate();
            return new UpdateCheckResult
            {
                IsUpdateAvailable = false,
                IsInstallerReady = false,
                Message = missingMessage,
                WasCheckSuccessful = false,
                UsedStorePath = false
            };
        }

        var appcastPublicKey = ResolveAppcastPublicKey();
        if (string.IsNullOrWhiteSpace(appcastPublicKey))
        {
            var missingPublicKeyMessage = $"No appcast public key configured. Set {AppcastPublicKeyEnvironmentVariable} for strict NetSparkle verification.";
            SaveSettings(missingPublicKeyMessage);
            ClearPendingUpdate();
            return new UpdateCheckResult
            {
                IsUpdateAvailable = false,
                IsInstallerReady = false,
                Message = missingPublicKeyMessage,
                WasCheckSuccessful = false,
                UsedStorePath = false
            };
        }

        try
        {
            var updater = GetOrCreateSparkleUpdater(appcastUrl, appcastPublicKey);
            var interactiveUiAvailable = updater.UIFactory is not null;
            if (fromStartupPolicy)
            {
                await EnsureUpdateLoopStartedAsync(updater, cancellationToken).ConfigureAwait(false);
            }

            var updateInfo = fromStartupPolicy
                ? await updater.CheckForUpdatesQuietly(ignoreSkippedVersions: false).ConfigureAwait(false)
                : await updater.CheckForUpdatesAtUserRequest(ignoreSkippedVersions: false).ConfigureAwait(false);

            var latest = updateInfo?.Updates?.FirstOrDefault();
            var latestVersion = latest?.Version ?? latest?.ShortVersion;
            var status = updateInfo?.Status ?? UpdateStatus.CouldNotDetermine;
            var isUpdateAvailable = status == UpdateStatus.UpdateAvailable && latest is not null;
            var installerReady = isUpdateAvailable && !string.IsNullOrWhiteSpace(await TryGetDownloadedInstallerPathAsync(updater, latest!).ConfigureAwait(false));

            RememberPendingUpdate(isUpdateAvailable ? latest : null);

            var message = BuildStatusMessage(status, latest, fromStartupPolicy, interactiveUiAvailable, installerReady);
            SaveSettings(message);

            return new UpdateCheckResult
            {
                IsUpdateAvailable = isUpdateAvailable,
                IsInstallerReady = installerReady,
                Message = message,
                LatestVersion = latestVersion,
                WasCheckSuccessful = true,
                UsedStorePath = false
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var failureMessage = $"NetSparkle update check failed: {ex.Message}";
            SaveSettings(failureMessage);
            return new UpdateCheckResult
            {
                IsUpdateAvailable = false,
                IsInstallerReady = false,
                Message = failureMessage,
                WasCheckSuccessful = false,
                UsedStorePath = false
            };
        }
    }

    private string ResolveAppcastUrl()
    {
        var configuredAppcastUrl = Environment.GetEnvironmentVariable(AppcastUrlEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configuredAppcastUrl))
        {
            updateSettings.AppcastUrl = NormalizeAppcastUrl(configuredAppcastUrl);
        }

        updateSettings.AppcastUrl = NormalizeAppcastUrl(updateSettings.AppcastUrl);
        return updateSettings.AppcastUrl;
    }

    public static string NormalizeAppcastUrl(string? appcastUrl)
    {
        if (string.IsNullOrWhiteSpace(appcastUrl))
        {
            return string.Empty;
        }

        var trimmed = appcastUrl.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return trimmed;
        }

        if (!string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        var pathSegments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (pathSegments.Length < 2 ||
            !string.Equals(pathSegments[0], LegacyPrivateGitHubOwner, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(pathSegments[1], RepositoryName, StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        pathSegments[0] = PublicGitHubOwner;
        var builder = new UriBuilder(uri)
        {
            Path = "/" + string.Join('/', pathSegments)
        };

        return builder.Uri.AbsoluteUri;
    }

    private string ResolveAppcastPublicKey()
    {
        var configuredPublicKey = Environment.GetEnvironmentVariable(AppcastPublicKeyEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configuredPublicKey))
        {
            updateSettings.AppcastPublicKey = configuredPublicKey.Trim();
        }

        return updateSettings.AppcastPublicKey?.Trim() ?? string.Empty;
    }

    private SparkleUpdater GetOrCreateSparkleUpdater(string appcastUrl, string appcastPublicKey)
    {
        lock (updaterSync)
        {
            ThrowIfDisposed();

            if (sparkleUpdater is not null &&
                string.Equals(sparkleUpdaterAppcastUrl, appcastUrl, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(sparkleUpdaterAppcastPublicKey, appcastPublicKey, StringComparison.Ordinal))
            {
                return sparkleUpdater;
            }

            if (sparkleUpdater?.IsUpdateLoopRunning == true)
            {
                sparkleUpdater.StopLoop();
            }

            sparkleUpdater?.Dispose();
            updateLoopStarted = false;

            var signatureVerifier = new Ed25519Checker(
                SecurityMode.Strict,
                appcastPublicKey,
                string.Empty,
                readFileBeingVerifiedInChunks: true,
                chunkSize: 25 * 1024 * 1024);

            var updater = new SparkleUpdater(appcastUrl, signatureVerifier)
            {
                CheckServerFileName = false,
                RelaunchAfterUpdate = true,
                ShouldKillParentProcessWhenStartingInstaller = true,
                ProcessIDToKillBeforeInstallerRuns = Environment.ProcessId.ToString(),
                UseNotificationToast = false,
                UIFactory = CreateUiFactoryIfAvailable()
            };

            ApplyInstalledVersionOverride(updater, installedComparisonVersion);

            sparkleUpdater = updater;
            sparkleUpdaterAppcastUrl = appcastUrl;
            sparkleUpdaterAppcastPublicKey = appcastPublicKey;
            return updater;
        }
    }

    private static UIFactory? CreateUiFactoryIfAvailable()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime)
        {
            return null;
        }

        var icon = TryLoadUpdateIcon();
        var useDarkTheme = UseDarkUpdaterTheme();
        var updateWindowBackground = new SolidColorBrush(Color.Parse(GetUpdateWindowBackgroundHex(useDarkTheme)));
        var uiFactory = icon is null ? new UIFactory() : new UIFactory(icon);
        uiFactory.UseStaticUpdateWindowBackgroundColor = true;
        uiFactory.UpdateWindowGridBackgroundBrush = updateWindowBackground;
        uiFactory.ReleaseNotesHTMLTemplate = BuildReleaseNotesHtmlTemplate(useDarkTheme);
        uiFactory.AdditionalReleaseNotesHeaderHTML = BuildReleaseNotesHeaderHtml(useDarkTheme);
        uiFactory.ProcessWindowAfterInit = (window, _) =>
        {
            try
            {
                if (Application.Current is not null)
                {
                    window.RequestedThemeVariant = Application.Current.ActualThemeVariant;
                }

                window.Background = updateWindowBackground;
                ScheduleUpdaterThemePasses(window, useDarkTheme);
            }
            catch
            {
            }
        };
        return uiFactory;
    }

    internal static bool UseDarkUpdaterTheme()
    {
        return Application.Current?.ActualThemeVariant == ThemeVariant.Dark;
    }

    internal static string GetUpdateWindowBackgroundHex(bool useDarkTheme)
    {
        return useDarkTheme ? "#111827" : "#E2E8F0";
    }

    internal static string GetUpdateContentBackgroundHex(bool useDarkTheme)
    {
        return useDarkTheme ? "#1F2937" : "#F8FAFC";
    }

    internal static string GetUpdateContentBorderHex(bool useDarkTheme)
    {
        return useDarkTheme ? "#374151" : "#CBD5E1";
    }

    internal static string BuildReleaseNotesHtmlTemplate(bool useDarkTheme)
    {
        var borderColor = useDarkTheme ? "#374151" : "#CBD5E1";
        var panelBackground = useDarkTheme ? "#1F2937" : "#F8FAFC";
        var textColor = useDarkTheme ? "#F8FAFC" : "#0F172A";
        var headerTextColor = useDarkTheme ? "#020617" : "#0F172A";

        return
            $"<div style=\"border: {borderColor} 1px solid; background: {panelBackground}; color: {textColor};\">" +
                $"<div style=\"background: {{3}}; background-color: {{3}}; color: {headerTextColor}; font-size: 16px; padding: 5px; padding-top: 4px; padding-bottom: 0;\">" +
                    "{0} ({1})" +
                $"</div><div style=\"padding: 5px; font-size: 12px; background: {panelBackground}; color: {textColor};\">{{2}}</div></div>";
    }

    internal static string BuildReleaseNotesHeaderHtml(bool useDarkTheme)
    {
        var panelBackground = useDarkTheme ? "#1F2937" : "#F8FAFC";
        var bodyText = useDarkTheme ? "#F8FAFC" : "#0F172A";
        var linkColor = useDarkTheme ? "#93C5FD" : "#1D4ED8";
        var codeBackground = useDarkTheme ? "#111827" : "#E2E8F0";
        var codeText = useDarkTheme ? "#E5E7EB" : "#1E293B";

        return
            "<style>" +
            $"html, body {{ margin: 0; min-height: 100%; background: {panelBackground}; color: {bodyText}; }} " +
            $"body {{ padding-left: 8px; padding-right: 8px; padding-top: 14px; padding-bottom: 12px; box-sizing: border-box; }} " +
            $"#rootcontainer, .release-notes-root {{ background: {panelBackground}; color: {bodyText}; }} " +
            $"h1, h2, h3, h4, h5, li, p, span, div {{ color: {bodyText}; }} " +
            "h1, h2, h3, h4, h5 { margin: 4px; margin-top: 8px; } " +
            "li, li li { margin: 4px; } " +
            "li, p { font-size: 18px; } " +
            "li p, li ul { margin-top: 0px; margin-bottom: 0px; } " +
            "ul { margin-top: 2px; margin-bottom: 2px; } " +
            $"a {{ color: {linkColor}; }} " +
            $"pre, code {{ background: {codeBackground}; color: {codeText}; }} " +
            "</style>";
    }

    private static WindowIcon? TryLoadUpdateIcon()
    {
        try
        {
            if (!AssetLoader.Exists(UpdateIconAssetUri))
            {
                return null;
            }

            using var stream = AssetLoader.Open(UpdateIconAssetUri);
            return new WindowIcon(stream);
        }
        catch
        {
            return null;
        }
    }

    private static void ApplyInstalledVersionOverride(SparkleUpdater updater, string installedVersion)
    {
        if (string.IsNullOrWhiteSpace(installedVersion))
        {
            return;
        }

        var configuration = SparkleUpdaterConfigurationProperty?.GetValue(updater);
        if (configuration is null || SparkleUpdaterInstalledVersionProperty is null)
        {
            return;
        }

        SparkleUpdaterInstalledVersionProperty.SetValue(configuration, installedVersion);
    }

    private async Task EnsureUpdateLoopStartedAsync(SparkleUpdater updater, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var shouldStartLoop = false;
        lock (updaterSync)
        {
            ThrowIfDisposed();

            if (!updateLoopStarted)
            {
                updateLoopStarted = true;
                shouldStartLoop = true;
            }
        }

        if (!shouldStartLoop || updater.IsUpdateLoopRunning)
        {
            return;
        }

        try
        {
            await updater
                .StartLoop(doInitialCheck: false, forceInitialCheck: false, checkFrequency: StartupCheckFrequency)
                .ConfigureAwait(false);
        }
        catch
        {
            lock (updaterSync)
            {
                updateLoopStarted = false;
            }

            throw;
        }
    }

    internal static string BuildStatusMessage(UpdateStatus status, AppCastItem? latest, bool fromStartupPolicy, bool interactiveUiAvailable, bool installerReady)
    {
        var latestVersion = latest?.Version ?? latest?.ShortVersion;
        return status switch
        {
            UpdateStatus.UpdateAvailable when !fromStartupPolicy && interactiveUiAvailable && !string.IsNullOrWhiteSpace(latestVersion)
                => $"Update review opened for {latestVersion}. Follow the updater prompts to download and install.",
            UpdateStatus.UpdateAvailable when !fromStartupPolicy && interactiveUiAvailable
                => "Update review opened. Follow the updater prompts to download and install.",
            UpdateStatus.UpdateAvailable when installerReady && !string.IsNullOrWhiteSpace(latestVersion)
                => $"Update ready to install: {latestVersion}. Click Update in the header to install.",
            UpdateStatus.UpdateAvailable when installerReady
                => "Update ready to install. Click Update in the header to install.",
            UpdateStatus.UpdateAvailable when !fromStartupPolicy && !interactiveUiAvailable && !string.IsNullOrWhiteSpace(latestVersion)
                => $"Update available: {latestVersion}.",
            UpdateStatus.UpdateAvailable when !fromStartupPolicy && !interactiveUiAvailable
                => "Update available.",
            UpdateStatus.UpdateAvailable when !string.IsNullOrWhiteSpace(latestVersion)
                => $"Update available: {latestVersion}. Click Update in the header to download and install.",
            UpdateStatus.UpdateAvailable
                => "Update available. Click Update in the header to download and install.",
            UpdateStatus.UpdateNotAvailable
                => "You already have the latest available version.",
            UpdateStatus.UserSkipped
                => "An update is available but currently marked as skipped.",
            _ => "Could not determine update availability from the appcast feed."
        };
    }

    private string ResolveInstalledComparisonVersion()
    {
        var sourceVersion = string.IsNullOrWhiteSpace(versionOverrideForTesting)
            ? actualCurrentVersion
            : versionOverrideForTesting;

        return AppVersionProvider.NormalizeForUpdateComparison(sourceVersion);
    }

    private AppCastItem? GetPendingUpdate()
    {
        lock (pendingUpdateSync)
        {
            return pendingUpdateItem;
        }
    }

    private void RememberPendingUpdate(AppCastItem? item)
    {
        lock (pendingUpdateSync)
        {
            pendingUpdateItem = item;
        }
    }

    private void ClearPendingUpdate()
    {
        RememberPendingUpdate(null);
    }

    private static async Task<string?> TryGetDownloadedInstallerPathAsync(SparkleUpdater updater, AppCastItem item)
    {
        var path = await updater.GetDownloadPathForAppCastItem(item).ConfigureAwait(false);
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path)
            ? path
            : null;
    }

    private static string? GetVersionLabel(AppCastItem? item)
    {
        return item?.Version ?? item?.ShortVersion;
    }

    private static string BuildInstallStartedMessage(string? latestVersion, bool usedCachedInstaller)
    {
        var prefix = usedCachedInstaller ? "Installing downloaded update" : "Launching downloaded update installer";
        return !string.IsNullOrWhiteSpace(latestVersion)
            ? $"{prefix} for {latestVersion}. NWS Helper will close and restart if the installer supports relaunch."
            : $"{prefix}. NWS Helper will close and restart if the installer supports relaunch.";
    }

    private static void ScheduleUpdaterThemePasses(Window window, bool useDarkTheme)
    {
        void ApplyThemePass()
        {
            TryApplyUpdaterThemeToWindow(window, useDarkTheme);
        }

        ApplyThemePass();
        Dispatcher.UIThread.Post(ApplyThemePass, DispatcherPriority.Loaded);
        Dispatcher.UIThread.Post(ApplyThemePass, DispatcherPriority.Background);

        void HandleOpened(object? sender, EventArgs args)
        {
            window.Opened -= HandleOpened;
            ApplyThemePass();
            Dispatcher.UIThread.Post(ApplyThemePass, DispatcherPriority.Loaded);
            Dispatcher.UIThread.Post(ApplyThemePass, DispatcherPriority.Background);
        }

        window.Opened += HandleOpened;
    }

    private static void TryApplyUpdaterThemeToWindow(Window window, bool useDarkTheme)
    {
        try
        {
            ApplyUpdaterThemeToWindow(window, useDarkTheme);
        }
        catch
        {
        }
    }

    private static void ApplyUpdaterThemeToWindow(Window window, bool useDarkTheme)
    {
        var contentBackground = new SolidColorBrush(Color.Parse(GetUpdateContentBackgroundHex(useDarkTheme)));
        var contentBorder = new SolidColorBrush(Color.Parse(GetUpdateContentBorderHex(useDarkTheme)));
        var foreground = new SolidColorBrush(useDarkTheme ? Color.Parse("#F8FAFC") : Color.Parse("#0F172A"));

        ApplyBrushIfPresent(window, "Foreground", contentBackground: null, foreground: foreground);

        foreach (var visual in window.GetVisualDescendants())
        {
            switch (visual)
            {
                case Border border:
                    ApplyBrushIfPresent(border, "Background", contentBackground);
                    ApplyBrushIfPresent(border, "BorderBrush", contentBorder);
                    break;
                case ScrollViewer scrollViewer:
                    ApplyBrushIfPresent(scrollViewer, "Background", contentBackground);
                    ApplyBrushIfPresent(scrollViewer, "BorderBrush", contentBorder);
                    break;
                case Panel panel when panel.GetType().Name is "Grid" or "StackPanel":
                    ApplyBrushIfPresent(panel, "Background", contentBackground);
                    break;
            }

            var typeName = visual.GetType().Name;
            if (typeName is "HtmlLabel" or "HtmlControl")
            {
                ApplyBrushIfPresent(visual, "Background", contentBackground);
                ApplyBrushIfPresent(visual, "BorderBrush", contentBorder);
                ApplyBrushIfPresent(visual, "Foreground", contentBackground: null, foreground: foreground);
            }
        }
    }

    private static void ApplyBrushIfPresent(object target, string propertyName, IBrush? contentBackground = null, IBrush? foreground = null)
    {
        var brush = foreground ?? contentBackground;
        if (brush is null)
        {
            return;
        }

        var property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        if (property is null || !property.CanWrite || !property.PropertyType.IsAssignableFrom(typeof(IBrush)))
        {
            return;
        }

        try
        {
            property.SetValue(target, brush);
        }
        catch
        {
        }
    }

    private static Task InstallUpdateAsync(SparkleUpdater updater, AppCastItem item, string installerPath)
    {
        return updater.InstallUpdate(item, installerPath);
    }

    private GuiUpdateSettings LoadSettings()
    {
        try
        {
            var settings = configurationStore.Load().Updates;
            return settings ?? new GuiUpdateSettings();
        }
        catch
        {
            return new GuiUpdateSettings();
        }
    }

    private void SaveSettings(string status)
    {
        try
        {
            updateSettings.LastCheckedUtc = DateTimeOffset.UtcNow;
            updateSettings.LastCheckStatus = status;
            var document = configurationStore.Load();
            document.Updates = updateSettings;
            configurationStore.Save(document);
        }
        catch
        {
        }
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(NetSparkleUpdateService));
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        lock (updaterSync)
        {
            if (disposed)
            {
                return;
            }

            if (sparkleUpdater?.IsUpdateLoopRunning == true)
            {
                sparkleUpdater.StopLoop();
            }

            sparkleUpdater?.Dispose();
            sparkleUpdater = null;
            sparkleUpdaterAppcastUrl = null;
            sparkleUpdaterAppcastPublicKey = null;
            updateLoopStarted = false;
            disposed = true;
        }
    }
}

