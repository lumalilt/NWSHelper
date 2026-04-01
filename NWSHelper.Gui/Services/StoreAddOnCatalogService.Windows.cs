#if WINDOWS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Services.Store;
using WinRT.Interop;

namespace NWSHelper.Gui.Services;

public sealed class WindowsStoreAddOnCatalogService : IStoreAddOnCatalogService
{
    private readonly Func<nint>? ownerWindowHandleProvider;

    public WindowsStoreAddOnCatalogService(Func<nint>? ownerWindowHandleProvider = null)
    {
        this.ownerWindowHandleProvider = ownerWindowHandleProvider;
    }

    public async Task<StoreAddOnCatalogResult> GetCatalogAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 14393))
        {
            return StoreAddOnCatalogResult.CreateUnavailable("Microsoft Store add-ons require Windows 10 Anniversary Update or later.");
        }

        try
        {
            var storeContext = CreateStoreContext();
            var queryResult = await storeContext.GetAssociatedStoreProductsAsync(["Durable"]);
            cancellationToken.ThrowIfCancellationRequested();

            if (queryResult.ExtendedError is not null)
            {
                return StoreAddOnCatalogResult.CreateUnavailable($"Microsoft Store add-ons could not be loaded: {queryResult.ExtendedError.Message}");
            }

            var offers = queryResult.Products.Values
                .OrderBy(product => product.Title, StringComparer.CurrentCultureIgnoreCase)
                .Select(MapOffer)
                .ToArray();

            var message = offers.Length switch
            {
                0 => "No Microsoft Store durable add-ons are currently associated with this app.",
                1 => "1 Microsoft Store add-on is available for this install.",
                _ => $"{offers.Length} Microsoft Store add-ons are available for this install."
            };

            return StoreAddOnCatalogResult.CreateAvailable(offers, message);
        }
        catch (Exception ex)
        {
            return StoreAddOnCatalogResult.CreateUnavailable($"Microsoft Store add-ons could not be loaded: {ex.Message}");
        }
    }

    public async Task<StoreAddOnPurchaseResult> PurchaseAsync(string storeId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(storeId))
        {
            return StoreAddOnPurchaseResult.CreateFailure("The selected Microsoft Store add-on is missing a Store ID.");
        }

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 14393))
        {
            return StoreAddOnPurchaseResult.CreateFailure("Microsoft Store purchases require Windows 10 Anniversary Update or later.");
        }

        var ownerWindowHandle = ownerWindowHandleProvider?.Invoke() ?? 0;
        if (ownerWindowHandle == 0)
        {
            return StoreAddOnPurchaseResult.CreateFailure("Microsoft Store purchase UI could not be initialized for this window.");
        }

        try
        {
            var storeContext = CreateStoreContext(ownerWindowHandle);
            var queryResult = await storeContext.GetAssociatedStoreProductsAsync(["Durable"]);
            cancellationToken.ThrowIfCancellationRequested();

            if (queryResult.ExtendedError is not null)
            {
                return StoreAddOnPurchaseResult.CreateFailure($"Microsoft Store purchase could not start: {queryResult.ExtendedError.Message}");
            }

            var product = queryResult.Products.Values.FirstOrDefault(candidate => string.Equals(candidate.StoreId, storeId, StringComparison.OrdinalIgnoreCase));
            if (product is null)
            {
                return StoreAddOnPurchaseResult.CreateFailure("The selected Microsoft Store add-on is no longer available for this app.");
            }

            var purchaseResult = await product.RequestPurchaseAsync();
            cancellationToken.ThrowIfCancellationRequested();

            return purchaseResult.Status switch
            {
                StorePurchaseStatus.Succeeded => StoreAddOnPurchaseResult.CreateSuccess($"Purchased {product.Title} from Microsoft Store."),
                StorePurchaseStatus.AlreadyPurchased => StoreAddOnPurchaseResult.CreateSuccess($"{product.Title} is already owned for this Microsoft account.", alreadyOwned: true),
                StorePurchaseStatus.NotPurchased => StoreAddOnPurchaseResult.CreateCanceled($"Purchase canceled for {product.Title}.'".TrimEnd('\'')),
                StorePurchaseStatus.NetworkError => StoreAddOnPurchaseResult.CreateFailure($"Microsoft Store purchase failed because the network is unavailable for {product.Title}."),
                StorePurchaseStatus.ServerError => StoreAddOnPurchaseResult.CreateFailure($"Microsoft Store purchase failed on the Store service for {product.Title}."),
                _ => purchaseResult.ExtendedError is not null
                    ? StoreAddOnPurchaseResult.CreateFailure($"Microsoft Store purchase failed: {purchaseResult.ExtendedError.Message}")
                    : StoreAddOnPurchaseResult.CreateFailure($"Microsoft Store purchase did not complete for {product.Title}.")
            };
        }
        catch (Exception ex)
        {
            return StoreAddOnPurchaseResult.CreateFailure($"Microsoft Store purchase could not be completed: {ex.Message}");
        }
    }

    private StoreContext CreateStoreContext(nint ownerWindowHandle = 0)
    {
        var storeContext = StoreContext.GetDefault();
        var handle = ownerWindowHandle != 0 ? ownerWindowHandle : ownerWindowHandleProvider?.Invoke() ?? 0;
        if (handle != 0)
        {
            InitializeWithWindow.Initialize(storeContext, handle);
        }

        return storeContext;
    }

    private static StoreAddOnOffer MapOffer(StoreProduct product)
    {
        return new StoreAddOnOffer
        {
            StoreId = product.StoreId ?? string.Empty,
            InAppOfferToken = product.InAppOfferToken ?? string.Empty,
            Title = string.IsNullOrWhiteSpace(product.Title) ? product.StoreId ?? "Microsoft Store add-on" : product.Title,
            Description = product.Description ?? string.Empty,
            PriceText = product.Price?.FormattedPrice ?? "Price unavailable",
            IsOwned = product.IsInUserCollection
        };
    }
}
#endif