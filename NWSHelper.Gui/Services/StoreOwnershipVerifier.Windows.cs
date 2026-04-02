#if WINDOWS
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Services.Store;

namespace NWSHelper.Gui.Services;

public sealed class WindowsStoreOwnershipVerifier : IStoreOwnershipVerifier
{
    private readonly StoreOwnershipOptions options;

    public WindowsStoreOwnershipVerifier(StoreOwnershipOptions? options = null)
    {
        this.options = options ?? new StoreOwnershipOptions();
    }

    public async Task<StoreOwnershipVerificationResult> VerifyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 14393))
        {
            return StoreOwnershipVerificationResult.CreateFallback("Microsoft Store ownership verification requires Windows 10 Anniversary Update or later.");
        }

        try
        {
            var storeContext = StoreContext.GetDefault();
            if (options.ProductKind == StoreOwnedProductKind.DurableAddOn)
            {
                return await VerifyDurableAddOnAsync(storeContext, cancellationToken, allowImplicitOwnedSelection: true);
            }

            if (!options.IsProductKindConfigured)
            {
                var durableAddOnVerification = await TryVerifyOwnedDurableAddOnAsync(storeContext, cancellationToken);
                if (durableAddOnVerification is { IsVerified: true, IsOwned: true })
                {
                    return durableAddOnVerification;
                }
            }

            return await VerifyAppAsync(storeContext, cancellationToken);
        }
        catch (Exception ex)
        {
            return StoreOwnershipVerificationResult.CreateFallback($"Microsoft Store ownership check could not be completed: {ex.Message}");
        }
    }

    private static async Task<StoreOwnershipVerificationResult> VerifyAppAsync(StoreContext storeContext, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var appLicense = await storeContext.GetAppLicenseAsync();
        cancellationToken.ThrowIfCancellationRequested();

        if (appLicense is null)
        {
            return StoreOwnershipVerificationResult.CreateFallback("Microsoft Store did not return app license data for this install.");
        }

        var isOwned = appLicense.IsActive && !appLicense.IsTrial;
        var evidence = new StoreOwnershipEvidence
        {
            ProductKind = StoreOwnedProductKind.App,
            ProductStoreId = appLicense.SkuStoreId ?? string.Empty,
            SkuStoreId = appLicense.SkuStoreId ?? string.Empty,
            IsOwned = isOwned,
            IsTrial = appLicense.IsTrial,
            ExpirationDateUtc = appLicense.ExpirationDate,
            VerificationSource = "windows-store-license"
        };

        var message = isOwned
            ? "Microsoft Store ownership verified for the current app."
            : appLicense.IsTrial
                ? "Microsoft Store trial is active. Purchase the full Store product to remove the 30 new addresses per territory cap."
                : "No qualifying Microsoft Store purchase was found for this app.";

        return StoreOwnershipVerificationResult.CreateVerified(evidence, message);
    }

    private async Task<StoreOwnershipVerificationResult> VerifyDurableAddOnAsync(StoreContext storeContext, CancellationToken cancellationToken, bool allowImplicitOwnedSelection)
    {
        var candidates = await GetDurableAddOnCandidatesAsync(storeContext, cancellationToken);
        var candidate = StoreOwnershipSelectionRules.SelectDurableAddOnCandidate(options, candidates);
        if (candidate is null)
        {
            if (options.HasDurableIdentifier)
            {
                return StoreOwnershipVerificationResult.CreateFallback("The configured Microsoft Store durable add-on was not returned for this app.");
            }

            if (!allowImplicitOwnedSelection)
            {
                return StoreOwnershipVerificationResult.CreateFallback("Set NWSHELPER_STORE_PRODUCT_STORE_ID or NWSHELPER_STORE_IN_APP_OFFER_TOKEN to verify the Microsoft Store durable add-on.");
            }

            return StoreOwnershipVerificationResult.CreateFallback("No uniquely owned Microsoft Store durable add-on was returned for this app.");
        }

        var message = candidate.IsOwned
            ? options.HasDurableIdentifier
                ? "Microsoft Store ownership verified for the configured durable add-on."
                : "Microsoft Store ownership verified for the durable add-on."
            : options.HasDurableIdentifier
                ? "No qualifying Microsoft Store purchase was found for the configured durable add-on."
                : "No qualifying Microsoft Store purchase was found for the durable add-on.";

        return StoreOwnershipVerificationResult.CreateVerified(candidate, message);
    }

    private async Task<StoreOwnershipVerificationResult?> TryVerifyOwnedDurableAddOnAsync(StoreContext storeContext, CancellationToken cancellationToken)
    {
        var candidates = await GetDurableAddOnCandidatesAsync(storeContext, cancellationToken);
        var candidate = StoreOwnershipSelectionRules.SelectDurableAddOnCandidate(options, candidates);
        if (candidate is null)
        {
            return null;
        }

        var message = candidate.IsOwned
            ? "Microsoft Store ownership verified for the durable add-on."
            : "No qualifying Microsoft Store purchase was found for the durable add-on.";

        return StoreOwnershipVerificationResult.CreateVerified(candidate, message);
    }

    private static async Task<StoreOwnershipEvidence[]> GetDurableAddOnCandidatesAsync(StoreContext storeContext, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var queryResult = await storeContext.GetAssociatedStoreProductsAsync(["Durable"]);
        cancellationToken.ThrowIfCancellationRequested();

        return queryResult.Products.Values
            .Select(product => new StoreOwnershipEvidence
            {
                ProductKind = StoreOwnedProductKind.DurableAddOn,
                ProductStoreId = product.StoreId ?? string.Empty,
                InAppOfferToken = product.InAppOfferToken ?? string.Empty,
                SkuStoreId = product.Skus.FirstOrDefault()?.StoreId ?? string.Empty,
                IsOwned = product.IsInUserCollection,
                IsTrial = false,
                VerificationSource = "windows-store-license"
            })
            .ToArray();
    }
}
#endif