using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NWSHelper.Gui.Services;
using Xunit;

namespace NWSHelper.Tests;

public class SupportDiagnosticsExportServiceTests
{
    [Fact]
    public async Task ExportAsync_WritesStoreDiagnosticsAndRedactsSecrets()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "nwshelper-support-diag-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        var settingsPath = Path.Combine(tempDirectory, "gui-settings.json");
        new GuiConfigurationStore(settingsPath).Save(new GuiConfigurationDocument
        {
            Setup = new GuiSetupSettings
            {
                OpenAddressesApiToken = "oa-secret-token",
                BoundaryCsvPath = @"C:\input\Territories.csv"
            },
            Entitlement = new GuiEntitlementSettings
            {
                ActivationKey = "NWSH-SECRET",
                ActivationKeyHash = "HASH-SECRET",
                SignedToken = "JWT-SECRET"
            },
            Updates = new GuiUpdateSettings
            {
                AppcastPublicKey = "PUBKEY"
            }
        });

        var exportPath = Path.Combine(tempDirectory, "support-diagnostics.json");
        var service = new SupportDiagnosticsExportService(
            filePath: settingsPath,
            storeRuntimeContextProvider: new FakeStoreRuntimeContextProvider(),
            storeOwnershipVerifier: new FakeStoreOwnershipVerifier(),
            storeOwnershipOptionsAccessor: () => new StoreOwnershipOptions
            {
                ProductKind = StoreOwnedProductKind.DurableAddOn,
                IsProductKindConfigured = true,
                ProductStoreId = "9NWSH-UNLIMITED",
                InAppOfferToken = "unlimited_addresses"
            });

        var result = await service.ExportAsync(exportPath, new SupportDiagnosticsSnapshot
        {
            CurrentVersion = "1.0.0-test",
            IsStoreInstall = true,
            StatusMessage = "Store claim pending review.",
            AccountLinkStatusMessage = "Store claim pending review.",
            AccountLinkSnapshot = new AccountLinkSnapshot
            {
                Status = AccountLinkStateStatus.PendingReview,
                Email = "store@example.com",
                PurchaseSource = "store"
            },
            EntitlementSnapshot = EntitlementSnapshot.CreateDefaultFree("Test"),
            HasStoreContinuityPrompt = true,
            StoreContinuityPromptTitle = "Store claim pending review",
            StoreContinuityPromptMessage = "waiting for manual review",
            CanRestoreStorePurchase = true,
            StoreAddOnCatalogMessage = "1 Microsoft Store add-on is available for this install."
        }, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(File.Exists(exportPath));

        var json = File.ReadAllText(exportPath);
        Assert.Contains("9NWSH-UNLIMITED", json, StringComparison.Ordinal);
        Assert.Contains("unlimited_addresses", json, StringComparison.Ordinal);
        Assert.DoesNotContain("oa-secret-token", json, StringComparison.Ordinal);
        Assert.DoesNotContain("NWSH-SECRET", json, StringComparison.Ordinal);
        Assert.DoesNotContain("HASH-SECRET", json, StringComparison.Ordinal);
        Assert.DoesNotContain("JWT-SECRET", json, StringComparison.Ordinal);
        Assert.Contains("openAddressesApiTokenConfigured", json, StringComparison.Ordinal);
        Assert.Contains("activationKeyPresent", json, StringComparison.Ordinal);
        Assert.Contains("signedTokenPresent", json, StringComparison.Ordinal);
    }

    private sealed class FakeStoreRuntimeContextProvider : IStoreRuntimeContextProvider
    {
        public StoreRuntimeContext GetCurrent()
        {
            return new StoreRuntimeContext
            {
                IsPackaged = true,
                IsStoreInstall = true,
                ProofAuthority = StoreProofAuthority.Verified,
                DetectionSource = "test",
                PackageFamilyName = "NWSHelper_Test",
                ProcessPath = @"C:\Program Files\WindowsApps\NWSHelper.exe",
                CapturedAtUtc = DateTimeOffset.UtcNow,
                OwnershipEvidence = new StoreOwnershipEvidence
                {
                    ProductKind = StoreOwnedProductKind.DurableAddOn,
                    ProductStoreId = "9NWSH-UNLIMITED",
                    InAppOfferToken = "unlimited_addresses",
                    SkuStoreId = "SKU-0010",
                    IsOwned = true,
                    VerificationSource = "test"
                }
            };
        }
    }

    private sealed class FakeStoreOwnershipVerifier : IStoreOwnershipVerifier
    {
        public Task<StoreOwnershipVerificationResult> VerifyAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(StoreOwnershipVerificationResult.CreateVerified(
                new StoreOwnershipEvidence
                {
                    ProductKind = StoreOwnedProductKind.DurableAddOn,
                    ProductStoreId = "9NWSH-UNLIMITED",
                    InAppOfferToken = "unlimited_addresses",
                    SkuStoreId = "SKU-0010",
                    IsOwned = true,
                    VerificationSource = "test"
                },
                "verified"));
        }
    }
}