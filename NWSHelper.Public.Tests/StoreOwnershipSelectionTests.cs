using NWSHelper.Gui.Services;
using Xunit;

namespace NWSHelper.Tests;

public class StoreOwnershipSelectionTests
{
    [Fact]
    public void SelectDurableAddOnCandidate_WhenProductKindIsImplicit_PrefersSingleOwnedDurableAddOn()
    {
        var options = new StoreOwnershipOptions
        {
            ProductKind = StoreOwnedProductKind.App,
            IsProductKindConfigured = false
        };

        var selected = StoreOwnershipSelectionRules.SelectDurableAddOnCandidate(options,
        [
            new StoreOwnershipEvidence
            {
                ProductKind = StoreOwnedProductKind.DurableAddOn,
                ProductStoreId = "9NWSH-UNLIMITED",
                InAppOfferToken = "unlimited_addresses",
                SkuStoreId = "0010",
                IsOwned = true,
                VerificationSource = "test"
            }
        ]);

        Assert.NotNull(selected);
        Assert.Equal(StoreOwnedProductKind.DurableAddOn, selected!.ProductKind);
        Assert.Equal("9NWSH-UNLIMITED", selected.ProductStoreId);
    }

    [Fact]
    public void SelectDurableAddOnCandidate_WhenProductKindIsExplicitApp_DoesNotAutoSelectDurableAddOn()
    {
        var options = new StoreOwnershipOptions
        {
            ProductKind = StoreOwnedProductKind.App,
            IsProductKindConfigured = true
        };

        var selected = StoreOwnershipSelectionRules.SelectDurableAddOnCandidate(options,
        [
            new StoreOwnershipEvidence
            {
                ProductKind = StoreOwnedProductKind.DurableAddOn,
                ProductStoreId = "9NWSH-UNLIMITED",
                InAppOfferToken = "unlimited_addresses",
                SkuStoreId = "0010",
                IsOwned = true,
                VerificationSource = "test"
            }
        ]);

        Assert.Null(selected);
    }

    [Fact]
    public void SelectDurableAddOnCandidate_WhenDurableIdentifierConfigured_MatchesByStoreIdOrSku()
    {
        var options = new StoreOwnershipOptions
        {
            ProductKind = StoreOwnedProductKind.DurableAddOn,
            IsProductKindConfigured = true,
            ProductStoreId = "SKU-0010"
        };

        var selected = StoreOwnershipSelectionRules.SelectDurableAddOnCandidate(options,
        [
            new StoreOwnershipEvidence
            {
                ProductKind = StoreOwnedProductKind.DurableAddOn,
                ProductStoreId = "9NWSH-UNLIMITED",
                InAppOfferToken = "unlimited_addresses",
                SkuStoreId = "SKU-0010",
                IsOwned = true,
                VerificationSource = "test"
            }
        ]);

        Assert.NotNull(selected);
        Assert.Equal("SKU-0010", selected!.SkuStoreId);
    }

    [Fact]
    public void SelectDurableAddOnCandidate_WhenMultipleOwnedDurablesExistWithoutIdentifiers_ReturnsNull()
    {
        var options = new StoreOwnershipOptions
        {
            ProductKind = StoreOwnedProductKind.DurableAddOn,
            IsProductKindConfigured = true
        };

        var selected = StoreOwnershipSelectionRules.SelectDurableAddOnCandidate(options,
        [
            new StoreOwnershipEvidence
            {
                ProductKind = StoreOwnedProductKind.DurableAddOn,
                ProductStoreId = "9NWSH-UNLIMITED",
                IsOwned = true,
                VerificationSource = "test"
            },
            new StoreOwnershipEvidence
            {
                ProductKind = StoreOwnedProductKind.DurableAddOn,
                ProductStoreId = "9NWSH-OTHER",
                IsOwned = true,
                VerificationSource = "test"
            }
        ]);

        Assert.Null(selected);
    }
}