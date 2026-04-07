using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NWSHelper.Core.Models;
using NWSHelper.Gui.Services;
using Xunit;

namespace NWSHelper.Tests;

public class AccountLinkServiceTests
{
    [Fact]
    public async Task RefreshStatusAsync_WhenActivationKeyWasRevoked_PersistsLinkedUnlimitedEntitlement()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "nwshelper-account-link-override", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var settingsPath = Path.Combine(tempDirectory, "gui-settings.json");

        try
        {
            var store = new GuiConfigurationStore(settingsPath);
            store.Save(new GuiConfigurationDocument
            {
                Entitlement = new GuiEntitlementSettings
                {
                    ActivationKey = "NWSH-OLD-KEY",
                    ActivationKeyHash = "HASH",
                    BasePlanCode = EntitlementProductCodes.FreeBasePlan,
                    AddOnCodes = Array.Empty<string>(),
                    MaxNewAddressesPerTerritory = 30,
                    LastValidatedUtc = DateTimeOffset.UtcNow.AddDays(-1),
                    ValidationSource = "RevokedOnline",
                    SignedToken = "old-token",
                    AccountLink = new GuiAccountLinkSettings
                    {
                        Status = AccountLinkStateStatus.Linked,
                        AccountId = "acct_store",
                        Email = "store@example.com",
                        PurchaseSource = "store",
                        LinkedAtUtc = DateTimeOffset.UtcNow.AddDays(-2),
                        LastSyncUtc = DateTimeOffset.UtcNow.AddDays(-1)
                    }
                }
            });

            using var httpClient = new HttpClient(new StubHttpMessageHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {
                          "message": "Account link status refreshed.",
                          "accountLink": {
                            "status": "linked",
                            "accountId": "acct_store",
                            "email": "store@example.com",
                            "purchaseSource": "store",
                            "linkedAtUtc": "2026-04-04T02:06:13.883Z",
                            "lastSyncUtc": "2026-04-07T22:04:40.105Z"
                          },
                          "entitlement": {
                            "basePlanCode": "free",
                            "addOnCodes": ["unlimited_addresses"],
                            "maxNewAddressesPerTerritory": null,
                            "expiresUtc": "2027-12-31T23:59:59Z"
                          },
                          "signedToken": "bridge-token"
                        }
                        """,
                        Encoding.UTF8,
                        "application/json")
                }));

            var accountLinkService = new AccountLinkService(
                filePath: settingsPath,
                httpClient: httpClient,
                options: new AccountLinkOptions
                {
                    SupabaseUrl = "https://example.test",
                    SupabaseAnonKey = "anon",
                    EntitlementsMePath = "functions/v1/entitlements-me"
                });

            var entitlementService = new SupabaseEntitlementService(
                filePath: settingsPath,
                options: new SupabaseActivationOptions
                {
                    SupabaseUrl = "https://example.test",
                    SupabaseAnonKey = "anon",
                    TokenSigningSecret = string.Empty
                });

            var result = await accountLinkService.RefreshStatusAsync(CancellationToken.None);
            var snapshot = entitlementService.GetSnapshot();

            Assert.True(result.IsSuccess);
            Assert.NotNull(result.EntitlementSnapshot);
            Assert.True(result.EntitlementSnapshot!.HasUnlimitedAddressesAddOn);
            Assert.Equal("AccountLink", result.EntitlementSnapshot.ValidationSource);
            Assert.True(snapshot.HasUnlimitedAddressesAddOn);
            Assert.Null(snapshot.MaxNewAddressesPerTerritory);
            Assert.Equal("AccountLink", snapshot.ValidationSource);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(handler(request));
        }
    }
}