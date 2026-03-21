using System;
using System.Threading;
using System.Threading.Tasks;

namespace NWSHelper.Gui.Services;

public enum StoreOwnedProductKind
{
    App,
    DurableAddOn
}

public sealed class StoreOwnershipOptions
{
    public StoreOwnedProductKind ProductKind { get; init; } = ReadProductKind(Environment.GetEnvironmentVariable("NWSHELPER_STORE_PRODUCT_KIND"));

    public string ProductStoreId { get; init; } = (Environment.GetEnvironmentVariable("NWSHELPER_STORE_PRODUCT_STORE_ID") ?? string.Empty).Trim();

    public string InAppOfferToken { get; init; } = (Environment.GetEnvironmentVariable("NWSHELPER_STORE_IN_APP_OFFER_TOKEN") ?? string.Empty).Trim();

    public bool HasDurableIdentifier =>
        !string.IsNullOrWhiteSpace(ProductStoreId) ||
        !string.IsNullOrWhiteSpace(InAppOfferToken);

    private static StoreOwnedProductKind ReadProductKind(string? rawValue)
    {
        var normalized = (rawValue ?? string.Empty).Trim();
        return normalized.Equals("DurableAddOn", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("Durable", StringComparison.OrdinalIgnoreCase)
            ? StoreOwnedProductKind.DurableAddOn
            : StoreOwnedProductKind.App;
    }
}

public sealed class StoreOwnershipEvidence
{
    public StoreOwnedProductKind ProductKind { get; init; }

    public string ProductStoreId { get; init; } = string.Empty;

    public string InAppOfferToken { get; init; } = string.Empty;

    public string SkuStoreId { get; init; } = string.Empty;

    public bool IsOwned { get; init; }

    public bool IsTrial { get; init; }

    public DateTimeOffset? ExpirationDateUtc { get; init; }

    public string VerificationSource { get; init; } = string.Empty;
}

public sealed class StoreOwnershipVerificationResult
{
    public bool IsVerified { get; init; }

    public bool IsOwned { get; init; }

    public bool IsTrial { get; init; }

    public bool AllowHeuristicFallback { get; init; }

    public string Message { get; init; } = string.Empty;

    public StoreOwnershipEvidence? Evidence { get; init; }

    public static StoreOwnershipVerificationResult CreateFallback(string message) =>
        new()
        {
            AllowHeuristicFallback = true,
            Message = message
        };

    public static StoreOwnershipVerificationResult CreateVerified(StoreOwnershipEvidence evidence, string message) =>
        new()
        {
            IsVerified = true,
            IsOwned = evidence.IsOwned,
            IsTrial = evidence.IsTrial,
            AllowHeuristicFallback = false,
            Message = message,
            Evidence = evidence
        };
}

public interface IStoreOwnershipVerifier
{
    Task<StoreOwnershipVerificationResult> VerifyAsync(CancellationToken cancellationToken);
}

public sealed class NoOpStoreOwnershipVerifier : IStoreOwnershipVerifier
{
    public Task<StoreOwnershipVerificationResult> VerifyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(StoreOwnershipVerificationResult.CreateFallback("Real Microsoft Store ownership verification is not available in this build."));
    }
}

public static class StoreOwnershipVerifierFactory
{
    public static IStoreOwnershipVerifier CreateDefault(StoreOwnershipOptions? options = null)
    {
#if WINDOWS
        return new WindowsStoreOwnershipVerifier(options);
#else
        return new NoOpStoreOwnershipVerifier();
#endif
    }
}