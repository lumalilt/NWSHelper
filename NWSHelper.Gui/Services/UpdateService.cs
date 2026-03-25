using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NetSparkleUpdater;
using NetSparkleUpdater.Enums;
using NetSparkleUpdater.SignatureVerifiers;

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

    public string Message { get; init; } = string.Empty;

    public string? LatestVersion { get; init; }

    public bool UsedStorePath { get; init; }
}

public interface IUpdateService
{
    string CurrentVersion { get; }

    bool IsStoreInstall { get; }

    bool AutoUpdateEnabled { get; set; }

    Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken);

    Task<UpdateCheckResult> CheckForUpdatesOnStartupAsync(CancellationToken cancellationToken);
}

public sealed class NetSparkleUpdateService : IUpdateService, IDisposable
{
    private const string AppcastUrlEnvironmentVariable = "NWSHELPER_APPCAST_URL";
    private const string AppcastPublicKeyEnvironmentVariable = "NWSHELPER_APPCAST_PUBLIC_KEY";
    private static readonly TimeSpan StartupCheckFrequency = TimeSpan.FromHours(12);
    private static readonly PropertyInfo? SparkleUpdaterConfigurationProperty = typeof(SparkleUpdater).GetProperty("Configuration", BindingFlags.Instance | BindingFlags.Public);
    private static readonly PropertyInfo? SparkleUpdaterInstalledVersionProperty = SparkleUpdaterConfigurationProperty?.PropertyType.GetProperty("InstalledVersion", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private readonly GuiConfigurationStore configurationStore;
    private readonly GuiUpdateSettings updateSettings;
    private readonly object updaterSync = new();
    private readonly string installedComparisonVersion;

    private SparkleUpdater? sparkleUpdater;
    private string? sparkleUpdaterAppcastUrl;
    private string? sparkleUpdaterAppcastPublicKey;
    private bool updateLoopStarted;
    private bool disposed;

    public NetSparkleUpdateService(string? filePath = null, IStoreRuntimeContextProvider? storeRuntimeContextProvider = null, string? currentVersionOverride = null)
    {
        configurationStore = new GuiConfigurationStore(filePath);
        updateSettings = LoadSettings();
        var storeRuntimeContext = (storeRuntimeContextProvider ?? new StoreRuntimeContextProvider()).GetCurrent();
        IsStoreInstall = storeRuntimeContext.IsStoreInstall;
        CurrentVersion = string.IsNullOrWhiteSpace(currentVersionOverride)
            ? AppVersionProvider.GetDisplayVersion()
            : currentVersionOverride.Trim();
        installedComparisonVersion = AppVersionProvider.NormalizeForUpdateComparison(CurrentVersion);
    }

    public string CurrentVersion { get; }

    public bool IsStoreInstall { get; }

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

    private async Task<UpdateCheckResult> CheckForUpdatesInternalAsync(bool fromStartupPolicy, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        if (IsStoreInstall)
        {
            var message = "Store install detected. Use Microsoft Store servicing APIs for update checks.";
            SaveSettings(message);
            return new UpdateCheckResult
            {
                IsUpdateAvailable = false,
                Message = message,
                UsedStorePath = true
            };
        }

        if (fromStartupPolicy && !AutoUpdateEnabled)
        {
            var disabledMessage = "Auto-update checks are disabled in Settings.";
            SaveSettings(disabledMessage);
            return new UpdateCheckResult
            {
                IsUpdateAvailable = false,
                Message = disabledMessage,
                UsedStorePath = false
            };
        }

        var appcastUrl = ResolveAppcastUrl();
        if (string.IsNullOrWhiteSpace(appcastUrl))
        {
            var missingMessage = $"No appcast URL configured. Set {AppcastUrlEnvironmentVariable} for NetSparkle checks.";
            SaveSettings(missingMessage);
            return new UpdateCheckResult
            {
                IsUpdateAvailable = false,
                Message = missingMessage,
                UsedStorePath = false
            };
        }

        var appcastPublicKey = ResolveAppcastPublicKey();
        if (string.IsNullOrWhiteSpace(appcastPublicKey))
        {
            var missingPublicKeyMessage = $"No appcast public key configured. Set {AppcastPublicKeyEnvironmentVariable} for strict NetSparkle verification.";
            SaveSettings(missingPublicKeyMessage);
            return new UpdateCheckResult
            {
                IsUpdateAvailable = false,
                Message = missingPublicKeyMessage,
                UsedStorePath = false
            };
        }

        try
        {
            var updater = GetOrCreateSparkleUpdater(appcastUrl, appcastPublicKey);
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

            var message = BuildStatusMessage(status, latest);
            SaveSettings(message);

            return new UpdateCheckResult
            {
                IsUpdateAvailable = isUpdateAvailable,
                Message = message,
                LatestVersion = latestVersion,
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
                Message = failureMessage,
                UsedStorePath = false
            };
        }
    }

    private string ResolveAppcastUrl()
    {
        var configuredAppcastUrl = Environment.GetEnvironmentVariable(AppcastUrlEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configuredAppcastUrl))
        {
            updateSettings.AppcastUrl = configuredAppcastUrl.Trim();
        }

        return updateSettings.AppcastUrl?.Trim() ?? string.Empty;
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
                UseNotificationToast = false
            };

            ApplyInstalledVersionOverride(updater, installedComparisonVersion);

            sparkleUpdater = updater;
            sparkleUpdaterAppcastUrl = appcastUrl;
            sparkleUpdaterAppcastPublicKey = appcastPublicKey;
            return updater;
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

    private static string BuildStatusMessage(UpdateStatus status, AppCastItem? latest)
    {
        var latestVersion = latest?.Version ?? latest?.ShortVersion;
        return status switch
        {
            UpdateStatus.UpdateAvailable when !string.IsNullOrWhiteSpace(latestVersion)
                => $"Update available: {latestVersion}. Use Check for Updates to review and install.",
            UpdateStatus.UpdateAvailable
                => "Update available. Use Check for Updates to review and install.",
            UpdateStatus.UpdateNotAvailable
                => "You already have the latest available version.",
            UpdateStatus.UserSkipped
                => "An update is available but currently marked as skipped.",
            _ => "Could not determine update availability from the appcast feed."
        };
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
