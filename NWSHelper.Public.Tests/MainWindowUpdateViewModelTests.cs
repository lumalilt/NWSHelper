using System.Threading;
using System.Threading.Tasks;
using NWSHelper.Gui.Services;
using NWSHelper.Gui.ViewModels;
using Xunit;

namespace NWSHelper.Tests;

public class MainWindowUpdateViewModelTests
{
    [Fact]
    public async Task StartupUpdatePolicy_WhenUpdateIsAvailable_ShowsHeaderButton()
    {
        var updateService = new FakeUpdateService
        {
            NextCheckResult = new UpdateCheckResult
            {
                IsUpdateAvailable = true,
                IsInstallerReady = false,
                WasCheckSuccessful = true,
                LatestVersion = "1.0.16",
                Message = "Update available: 1.0.16. Click Update in the header to download and install."
            }
        };

        var viewModel = new MainWindowViewModel(updateService: updateService);

        await viewModel.RunStartupUpdatePolicyAsync();

        Assert.True(viewModel.ShowHeaderUpdateButton);
        Assert.True(viewModel.CanApplyAvailableUpdate);
        Assert.Equal("Download and install update 1.0.16", viewModel.UpdateButtonToolTip);
    }

    [Fact]
    public async Task ApplyAvailableUpdate_WhenInstallerStarts_ClearsHeaderButton()
    {
        var updateService = new FakeUpdateService
        {
            NextCheckResult = new UpdateCheckResult
            {
                IsUpdateAvailable = true,
                IsInstallerReady = true,
                WasCheckSuccessful = true,
                LatestVersion = "1.0.16",
                Message = "Update ready to install: 1.0.16. Click Update in the header to install."
            },
            NextInstallResult = new UpdateInstallResult
            {
                StartedInstaller = true,
                LatestVersion = "1.0.16",
                UsedCachedInstaller = true,
                Message = "Installing downloaded update for 1.0.16. NWS Helper will close and restart if the installer supports relaunch."
            }
        };

        var viewModel = new MainWindowViewModel(updateService: updateService);
        await viewModel.RunStartupUpdatePolicyAsync();

        await viewModel.ApplyAvailableUpdateCommand.ExecuteAsync(null);

        Assert.False(viewModel.ShowHeaderUpdateButton);
        Assert.Equal("Installing downloaded update for 1.0.16. NWS Helper will close and restart if the installer supports relaunch.", viewModel.StatusMessage);
    }

    [Fact]
    public void UpdateVersionOverrideForTesting_WhenSet_UpdatesServiceAndCanBeCleared()
    {
        var updateService = new FakeUpdateService { CurrentVersion = "1.0.16+build.123" };
        var viewModel = new MainWindowViewModel(updateService: updateService);

        viewModel.UpdateVersionOverrideForTesting = "1.0.9";

        Assert.Equal("1.0.9", updateService.VersionOverrideForTesting);
        Assert.True(viewModel.HasUpdateVersionOverrideForTesting);
        Assert.Contains("1.0.9", viewModel.UpdateStatusMessage);

        viewModel.ClearUpdateVersionOverrideForTestingCommand.Execute(null);

        Assert.Equal(string.Empty, updateService.VersionOverrideForTesting);
        Assert.False(viewModel.HasUpdateVersionOverrideForTesting);
        Assert.Contains("1.0.16+build.123", viewModel.UpdateStatusMessage);
    }

    private sealed class FakeUpdateService : IUpdateService
    {
        public string CurrentVersion { get; set; } = "1.0.0-test";

        public string VersionOverrideForTesting { get; set; } = string.Empty;

        public bool IsStoreInstall { get; set; }

        public bool AutoUpdateEnabled { get; set; } = true;

        public UpdateCheckResult NextCheckResult { get; set; } = new()
        {
            Message = "No updates.",
            WasCheckSuccessful = true
        };

        public UpdateInstallResult NextInstallResult { get; set; } = new()
        {
            Message = "noop"
        };

        public Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(NextCheckResult);
        }

        public Task<UpdateCheckResult> CheckForUpdatesOnStartupAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(NextCheckResult);
        }

        public Task<UpdateInstallResult> DownloadAndInstallPendingUpdateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(NextInstallResult);
        }
    }
}