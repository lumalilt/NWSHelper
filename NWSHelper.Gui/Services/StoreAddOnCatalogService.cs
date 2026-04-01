using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NWSHelper.Gui.Services;

public sealed class StoreAddOnOffer
{
    public string StoreId { get; init; } = string.Empty;

    public string InAppOfferToken { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public string PriceText { get; init; } = string.Empty;

    public bool IsOwned { get; init; }
}

public sealed class StoreAddOnCatalogResult
{
    public bool IsAvailable { get; init; }

    public string Message { get; init; } = string.Empty;

    public IReadOnlyList<StoreAddOnOffer> Offers { get; init; } = [];

    public static StoreAddOnCatalogResult CreateUnavailable(string message) =>
        new()
        {
            IsAvailable = false,
            Message = message
        };

    public static StoreAddOnCatalogResult CreateAvailable(IReadOnlyList<StoreAddOnOffer> offers, string message) =>
        new()
        {
            IsAvailable = true,
            Offers = offers,
            Message = message
        };
}

public sealed class StoreAddOnPurchaseResult
{
    public bool IsSuccess { get; init; }

    public bool IsCanceled { get; init; }

    public bool IsAlreadyOwned { get; init; }

    public string Message { get; init; } = string.Empty;

    public static StoreAddOnPurchaseResult CreateSuccess(string message, bool alreadyOwned = false) =>
        new()
        {
            IsSuccess = true,
            IsAlreadyOwned = alreadyOwned,
            Message = message
        };

    public static StoreAddOnPurchaseResult CreateCanceled(string message) =>
        new()
        {
            IsCanceled = true,
            Message = message
        };

    public static StoreAddOnPurchaseResult CreateFailure(string message) =>
        new()
        {
            Message = message
        };
}

public interface IStoreAddOnCatalogService
{
    Task<StoreAddOnCatalogResult> GetCatalogAsync(CancellationToken cancellationToken);

    Task<StoreAddOnPurchaseResult> PurchaseAsync(string storeId, CancellationToken cancellationToken);
}

public sealed class NoOpStoreAddOnCatalogService : IStoreAddOnCatalogService
{
    public Task<StoreAddOnCatalogResult> GetCatalogAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(StoreAddOnCatalogResult.CreateUnavailable("Microsoft Store add-ons are only available from a packaged Windows Store install."));
    }

    public Task<StoreAddOnPurchaseResult> PurchaseAsync(string storeId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(StoreAddOnPurchaseResult.CreateFailure("Microsoft Store purchases are not available in this build."));
    }
}

public static class StoreAddOnCatalogServiceFactory
{
    public static IStoreAddOnCatalogService CreateDefault(Func<nint>? ownerWindowHandleProvider = null)
    {
#if WINDOWS
        return new WindowsStoreAddOnCatalogService(ownerWindowHandleProvider);
#else
        return new NoOpStoreAddOnCatalogService();
#endif
    }
}