using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NWSHelper.Core.Models;
using NWSHelper.Gui.Services;
using NWSHelper.Gui.ViewModels;
using Xunit;

namespace NWSHelper.Tests;

public class StoreContinuityViewModelTests
{
    [Fact]
    public async Task StartupPolicy_WhenStoreInstallIsUnlinked_PromptsFromSettingsAndRequestsAttention()
    {
        var viewModel = CreateViewModel(updateService: new FakeUpdateService { IsStoreInstall = true });

        await viewModel.RunStartupStoreContinuityPolicyAsync();

        Assert.Equal(WorkflowStage.Settings, viewModel.CurrentStage);
        Assert.True(viewModel.HasStoreContinuityPrompt);
        Assert.Equal(1, viewModel.StoreContinuityAttentionRequestId);
        Assert.Equal("Protect Store purchase continuity", viewModel.StoreContinuityPromptTitle);
        Assert.Contains("direct install", viewModel.StoreContinuityPromptMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("direct-download access", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartupPolicy_WhenStoreSessionExists_AutoRestoresPurchase()
    {
        var accountLinkService = new FakeAccountLinkService
        {
            Snapshot = new AccountLinkSnapshot
            {
                Status = AccountLinkStateStatus.SignedIn,
                AccountId = "acct_store",
                Email = "store@example.com",
                LastSyncUtc = DateTimeOffset.UtcNow
            },
            RestoreStorePurchaseResult = new AccountLinkOperationResult
            {
                IsSuccess = true,
                Message = "Store purchase linked.",
                Snapshot = new AccountLinkSnapshot
                {
                    Status = AccountLinkStateStatus.Linked,
                    AccountId = "acct_store",
                    Email = "store@example.com",
                    PurchaseSource = "store",
                    LinkedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
                    LastSyncUtc = DateTimeOffset.UtcNow
                },
                EntitlementSnapshot = new EntitlementSnapshot
                {
                    BasePlanCode = EntitlementProductCodes.FreeBasePlan,
                    AddOnCodes = [EntitlementProductCodes.UnlimitedAddressesAddOn],
                    MaxNewAddressesPerTerritory = null,
                    LastValidatedUtc = DateTimeOffset.UtcNow,
                    ValidationSource = "Online"
                }
            }
        };

        var viewModel = CreateViewModel(
            accountLinkService: accountLinkService,
            updateService: new FakeUpdateService { IsStoreInstall = true });

        await viewModel.RunStartupStoreContinuityPolicyAsync();

        Assert.Equal(1, accountLinkService.RestoreStorePurchaseCalls);
        Assert.Equal(0, viewModel.StoreContinuityAttentionRequestId);
        Assert.True(viewModel.HasUnlimitedAddressesAddOn);
        Assert.False(viewModel.HasStoreContinuityPrompt);
        Assert.Equal("Store purchase linked.", viewModel.StatusMessage);
    }

    [Fact]
    public void PublicGuiMarkup_ContainsStoreContinuityAttentionBindingsAndStartupHook()
    {
        var settingsMarkup = File.ReadAllText(Path.Combine(GetRepositoryRoot(), "NWSHelper.Gui", "Views", "Stages", "SettingsStageView.axaml"));
        var settingsCodeBehind = File.ReadAllText(Path.Combine(GetRepositoryRoot(), "NWSHelper.Gui", "Views", "Stages", "SettingsStageView.axaml.cs"));
        var appMarkup = File.ReadAllText(Path.Combine(GetRepositoryRoot(), "NWSHelper.Gui", "App.axaml"));
        var mainWindowCodeBehind = File.ReadAllText(Path.Combine(GetRepositoryRoot(), "NWSHelper.Gui", "Views", "MainWindow.axaml.cs"));

        Assert.Contains("HasStoreContinuityPrompt", settingsMarkup, StringComparison.Ordinal);
        Assert.Contains("StoreContinuityPromptTitle", settingsMarkup, StringComparison.Ordinal);
        Assert.Contains("StoreContinuityPromptMessage", settingsMarkup, StringComparison.Ordinal);
        Assert.Contains("StoreContinuityPromptBanner", settingsMarkup, StringComparison.Ordinal);
        Assert.Contains("AccountLinkSection", settingsMarkup, StringComparison.Ordinal);
        Assert.Contains("AccountLinkEmailTextBox", settingsMarkup, StringComparison.Ordinal);
        Assert.Contains("store-continuity-prompt", settingsMarkup, StringComparison.Ordinal);
        Assert.Contains("StoreContinuityPromptBackgroundBrush", appMarkup, StringComparison.Ordinal);
        Assert.Contains("StoreContinuityAttentionRequestId", settingsCodeBehind, StringComparison.Ordinal);
        Assert.Contains("BringIntoView", settingsCodeBehind, StringComparison.Ordinal);
        Assert.Contains("PulseStoreContinuityPromptAsync", settingsCodeBehind, StringComparison.Ordinal);
        Assert.Contains("RunStartupStoreContinuityPolicyAsync", mainWindowCodeBehind, StringComparison.Ordinal);
    }

    private static MainWindowViewModel CreateViewModel(
        IEntitlementService? entitlementService = null,
        IAccountLinkService? accountLinkService = null,
        IUpdateService? updateService = null)
    {
        return new MainWindowViewModel(
            themeService: new FakeThemeService(),
            setupSettingsService: new FakeSetupSettingsService(),
            entitlementService: entitlementService ?? new FakeEntitlementService(),
            accountLinkService: accountLinkService ?? new FakeAccountLinkService(),
            updateService: updateService ?? new FakeUpdateService(),
            settingsMigrationService: new FakeGuiSettingsMigrationService());
    }

    private static string GetRepositoryRoot()
    {
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    }

    private sealed class FakeThemeService : IThemeService
    {
        public AppThemePreference CurrentPreference { get; set; } = AppThemePreference.System;

        public void ApplyTheme(AppThemePreference preference)
        {
            CurrentPreference = preference;
        }
    }

    private sealed class FakeSetupSettingsService : ISetupSettingsService
    {
        public GuiSetupSettings? Load() => null;

        public void Save(GuiSetupSettings settings)
        {
        }
    }

    private sealed class FakeGuiSettingsMigrationService : IGuiSettingsMigrationService
    {
        public Task<GuiSettingsMigrationResult> ExportAsync(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new GuiSettingsMigrationResult { IsSuccess = true, Message = "ok" });
        }

        public Task<GuiSettingsMigrationResult> ImportAsync(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new GuiSettingsMigrationResult { IsSuccess = true, Message = "ok" });
        }
    }

    private sealed class FakeEntitlementService : IEntitlementService
    {
        public EntitlementSnapshot Snapshot { get; set; } = EntitlementSnapshot.CreateDefaultFree("Test");

        public EntitlementSnapshot GetSnapshot() => Snapshot;

        public Task<EntitlementSnapshot> RefreshAsync(CancellationToken cancellationToken, bool forceOnlineRevalidation = false)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Snapshot);
        }

        public Task<EntitlementActivationResult> ActivateAsync(string activationKey, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new EntitlementActivationResult
            {
                IsSuccess = false,
                Message = "not used",
                Snapshot = Snapshot
            });
        }
    }

    private sealed class FakeAccountLinkService : IAccountLinkService
    {
        public AccountLinkSnapshot Snapshot { get; set; } = AccountLinkSnapshot.CreateSignedOut();

        public AccountLinkOperationResult StartSignInResult { get; set; } = new()
        {
            IsSuccess = true,
            Message = "Sign-in link requested.",
            Snapshot = AccountLinkSnapshot.CreateSignedOut()
        };

        public AccountLinkOperationResult RefreshStatusResult { get; set; } = new()
        {
            IsSuccess = true,
            Message = "Account link status refreshed.",
            Snapshot = AccountLinkSnapshot.CreateSignedOut()
        };

        public AccountLinkOperationResult RestoreStorePurchaseResult { get; set; } = new()
        {
            IsSuccess = true,
            Message = "Store purchase restore completed.",
            Snapshot = AccountLinkSnapshot.CreateSignedOut()
        };

        public int RestoreStorePurchaseCalls { get; private set; }

        public AccountLinkSnapshot GetSnapshot() => Snapshot;

        public Task<AccountLinkOperationResult> SaveSnapshotAsync(AccountLinkSnapshot snapshot, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Snapshot = snapshot;
            return Task.FromResult(new AccountLinkOperationResult { IsSuccess = true, Message = "saved", Snapshot = snapshot });
        }

        public Task<AccountLinkOperationResult> ClearCachedLinkAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Snapshot = AccountLinkSnapshot.CreateSignedOut();
            return Task.FromResult(new AccountLinkOperationResult { IsSuccess = true, Message = "cleared", Snapshot = Snapshot });
        }

        public Task<AccountLinkOperationResult> StartSignInAsync(string email, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Snapshot = StartSignInResult.Snapshot;
            return Task.FromResult(StartSignInResult);
        }

        public Task<AccountLinkOperationResult> RefreshStatusAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Snapshot = RefreshStatusResult.Snapshot;
            return Task.FromResult(RefreshStatusResult);
        }

        public Task<AccountLinkOperationResult> RestoreStorePurchaseAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RestoreStorePurchaseCalls++;
            Snapshot = RestoreStorePurchaseResult.Snapshot;
            return Task.FromResult(RestoreStorePurchaseResult);
        }
    }

    private sealed class FakeUpdateService : IUpdateService
    {
        public string CurrentVersion { get; set; } = "1.0.0-test";

        public bool IsStoreInstall { get; set; }

        public bool AutoUpdateEnabled { get; set; } = true;

        public Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new UpdateCheckResult { Message = "No updates." });
        }

        public Task<UpdateCheckResult> CheckForUpdatesOnStartupAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new UpdateCheckResult { Message = "No updates." });
        }
    }
}