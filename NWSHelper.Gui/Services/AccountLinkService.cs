using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NWSHelper.Core.Models;
using NWSHelper.Core.Services;

namespace NWSHelper.Gui.Services;

public enum AccountLinkStateStatus
{
    SignedOut,
    AwaitingConfirmation,
    SignedIn,
    Linked,
    PendingReview,
    Failed
}

public sealed class GuiAccountLinkSettings
{
    public AccountLinkStateStatus Status { get; set; } = AccountLinkStateStatus.SignedOut;

    public string AccountId { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public string PurchaseSource { get; set; } = string.Empty;

    public DateTimeOffset? LinkedAtUtc { get; set; }

    public DateTimeOffset? LastSyncUtc { get; set; }

    public string LastError { get; set; } = string.Empty;
}

public sealed class AccountLinkSnapshot
{
    public AccountLinkStateStatus Status { get; init; } = AccountLinkStateStatus.SignedOut;

    public string AccountId { get; init; } = string.Empty;

    public string Email { get; init; } = string.Empty;

    public string PurchaseSource { get; init; } = string.Empty;

    public DateTimeOffset? LinkedAtUtc { get; init; }

    public DateTimeOffset? LastSyncUtc { get; init; }

    public string LastError { get; init; } = string.Empty;

    public bool HasState =>
        Status != AccountLinkStateStatus.SignedOut ||
        !string.IsNullOrWhiteSpace(AccountId) ||
        !string.IsNullOrWhiteSpace(Email) ||
        !string.IsNullOrWhiteSpace(PurchaseSource) ||
        LinkedAtUtc.HasValue ||
        LastSyncUtc.HasValue ||
        !string.IsNullOrWhiteSpace(LastError);

    public bool HasActiveSession =>
        Status == AccountLinkStateStatus.SignedIn ||
        Status == AccountLinkStateStatus.Linked ||
        Status == AccountLinkStateStatus.PendingReview;

    public static AccountLinkSnapshot CreateSignedOut(string lastError = "")
    {
        return new AccountLinkSnapshot
        {
            Status = AccountLinkStateStatus.SignedOut,
            LastError = lastError
        };
    }
}

public sealed class AccountLinkOperationResult
{
    public bool IsSuccess { get; init; }

    public string Message { get; init; } = string.Empty;

    public AccountLinkSnapshot Snapshot { get; init; } = AccountLinkSnapshot.CreateSignedOut();

    public EntitlementSnapshot? EntitlementSnapshot { get; init; }
}

public sealed class AccountLinkOptions
{
    public string SupabaseUrl { get; init; } = Environment.GetEnvironmentVariable("NWSHELPER_SUPABASE_URL") ?? string.Empty;

    public string SupabaseAnonKey { get; init; } = Environment.GetEnvironmentVariable("NWSHELPER_SUPABASE_ANON_KEY") ?? string.Empty;

    public string AuthStartPath { get; init; } = Environment.GetEnvironmentVariable("NWSHELPER_ACCOUNT_LINK_AUTH_START_PATH") ?? "functions/v1/auth-start";

    public string EntitlementsMePath { get; init; } = Environment.GetEnvironmentVariable("NWSHELPER_ACCOUNT_LINK_STATUS_PATH") ?? "functions/v1/entitlements-me";

    public string ClaimStorePath { get; init; } = Environment.GetEnvironmentVariable("NWSHELPER_ACCOUNT_LINK_CLAIM_STORE_PATH") ?? "functions/v1/claim-store";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(SupabaseUrl) && !string.IsNullOrWhiteSpace(SupabaseAnonKey);
}

public interface IAccountLinkService
{
    AccountLinkSnapshot GetSnapshot();

    Task<AccountLinkOperationResult> SaveSnapshotAsync(AccountLinkSnapshot snapshot, CancellationToken cancellationToken);

    Task<AccountLinkOperationResult> ClearCachedLinkAsync(CancellationToken cancellationToken);

    Task<AccountLinkOperationResult> StartSignInAsync(string email, CancellationToken cancellationToken);

    Task<AccountLinkOperationResult> RefreshStatusAsync(CancellationToken cancellationToken);

    Task<AccountLinkOperationResult> RestoreStorePurchaseAsync(CancellationToken cancellationToken);
}

public sealed class AccountLinkService : IAccountLinkService
{
    private static readonly JsonSerializerOptions RequestJsonSerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly GuiConfigurationStore configurationStore;
    private readonly HttpClient httpClient;
    private readonly AccountLinkOptions options;
    private readonly IStoreRuntimeContextProvider storeRuntimeContextProvider;
    private readonly IStoreOwnershipVerifier storeOwnershipVerifier;

    public AccountLinkService(
        string? filePath = null,
        HttpClient? httpClient = null,
        AccountLinkOptions? options = null,
        IStoreRuntimeContextProvider? storeRuntimeContextProvider = null,
        IStoreOwnershipVerifier? storeOwnershipVerifier = null)
    {
        configurationStore = new GuiConfigurationStore(filePath);
        this.httpClient = httpClient ?? new HttpClient();
        this.options = options ?? new AccountLinkOptions();
        this.storeRuntimeContextProvider = storeRuntimeContextProvider ?? new StoreRuntimeContextProvider();
        this.storeOwnershipVerifier = storeOwnershipVerifier ?? StoreOwnershipVerifierFactory.CreateDefault();
    }

    public AccountLinkSnapshot GetSnapshot()
    {
        try
        {
            return ToSnapshot(configurationStore.Load().Entitlement?.AccountLink);
        }
        catch
        {
            return AccountLinkSnapshot.CreateSignedOut("LoadFailed");
        }
    }

    public Task<AccountLinkOperationResult> SaveSnapshotAsync(AccountLinkSnapshot snapshot, CancellationToken cancellationToken)
    {
        return PersistSnapshotAsync(snapshot, entitlementSnapshot: null, signedToken: string.Empty, cancellationToken);
    }

    private Task<AccountLinkOperationResult> PersistSnapshotAsync(
        AccountLinkSnapshot snapshot,
        EntitlementSnapshot? entitlementSnapshot,
        string signedToken,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var document = configurationStore.Load();
            var existingSettings = document.Entitlement ?? new GuiEntitlementSettings();
            var shouldPersistBridgeEntitlement = ShouldPersistBridgeEntitlement(existingSettings, entitlementSnapshot);
            document.Entitlement = shouldPersistBridgeEntitlement
                ? MergeEntitlementSettings(existingSettings, snapshot, entitlementSnapshot!, signedToken)
                : CloneWithUpdatedAccountLink(existingSettings, snapshot);
            configurationStore.Save(document);

            var persistedSnapshot = ToSnapshot(document.Entitlement.AccountLink);
            var visibleEntitlementSnapshot = FilterVisibleEntitlementSnapshot(existingSettings, entitlementSnapshot);
            return Task.FromResult(new AccountLinkOperationResult
            {
                IsSuccess = true,
                Message = persistedSnapshot.HasState
                    ? "Account link state saved."
                    : "Linked account cache cleared.",
                Snapshot = persistedSnapshot,
                EntitlementSnapshot = visibleEntitlementSnapshot
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new AccountLinkOperationResult
            {
                IsSuccess = false,
                Message = $"Could not persist account link state: {ex.Message}",
                Snapshot = GetSnapshot(),
                EntitlementSnapshot = FilterVisibleEntitlementSnapshot(GetStoredEntitlementSettings(), entitlementSnapshot)
            });
        }
    }

    public Task<AccountLinkOperationResult> ClearCachedLinkAsync(CancellationToken cancellationToken)
    {
        return SaveSnapshotAsync(AccountLinkSnapshot.CreateSignedOut(), cancellationToken);
    }

    public async Task<AccountLinkOperationResult> StartSignInAsync(string email, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedEmail = (email ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedEmail))
        {
            return CreateFailureResult("Enter an email before requesting an account sign-in link.");
        }

        if (!options.IsConfigured)
        {
            return CreateFailureResult("Account linking is not configured. Set NWSHELPER_SUPABASE_URL and NWSHELPER_SUPABASE_ANON_KEY.");
        }

        try
        {
            var storeRuntimeContext = storeRuntimeContextProvider.GetCurrent();
            var (isSuccess, responseJson, statusCode) = await SendRequestAsync(
                HttpMethod.Post,
                BuildUri(options.AuthStartPath),
                new
                {
                    email = normalizedEmail,
                    installationIdHash = ComputeInstallationHash(),
                    appVersion = AppVersionProvider.GetDisplayVersion(),
                    isStoreInstall = storeRuntimeContext.IsStoreInstall
                },
                cancellationToken);

            var responseMessage = TryExtractResponseMessage(responseJson)
                ?? (isSuccess
                    ? "Sign-in link requested. Check your email to continue."
                    : $"Account sign-in request failed with HTTP {statusCode}.");

            var currentSnapshot = GetSnapshot();
            var parsedSnapshot = ParseAccountLinkSnapshot(responseJson);
            var entitlementSnapshot = NormalizeBridgeEntitlementSnapshot(ParseEntitlementSnapshot(responseJson));
            var signedToken = TryExtractSignedToken(responseJson) ?? string.Empty;

            if (!isSuccess)
            {
                if (parsedSnapshot is not null)
                {
                    return await PersistResultAsync(MergeSnapshot(parsedSnapshot, currentSnapshot, normalizedEmail), responseMessage, false, entitlementSnapshot, signedToken, cancellationToken);
                }

                return CreateFailureResult(responseMessage, currentSnapshot, FilterVisibleEntitlementSnapshot(GetStoredEntitlementSettings(), entitlementSnapshot));
            }

            var nextSnapshot = parsedSnapshot is null
                ? new AccountLinkSnapshot
                {
                    Status = AccountLinkStateStatus.AwaitingConfirmation,
                    Email = normalizedEmail,
                    LastSyncUtc = DateTimeOffset.UtcNow
                }
                : MergeSnapshot(parsedSnapshot, currentSnapshot, normalizedEmail);

            return await PersistResultAsync(nextSnapshot, responseMessage, true, entitlementSnapshot, signedToken, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return CreateFailureResult($"Account sign-in request failed: {ex.Message}");
        }
    }

    public async Task<AccountLinkOperationResult> RefreshStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!options.IsConfigured)
        {
            return CreateFailureResult("Account linking is not configured. Set NWSHELPER_SUPABASE_URL and NWSHELPER_SUPABASE_ANON_KEY.");
        }

        try
        {
            var requestUri = BuildUriWithQuery(options.EntitlementsMePath, ComputeInstallationHash(), AppVersionProvider.GetDisplayVersion());
            var (isSuccess, responseJson, statusCode) = await SendRequestAsync(HttpMethod.Get, requestUri, null, cancellationToken);

            var responseMessage = TryExtractResponseMessage(responseJson)
                ?? (isSuccess
                    ? "Account link status refreshed."
                    : $"Account link refresh failed with HTTP {statusCode}.");

            var currentSnapshot = GetSnapshot();
            var parsedSnapshot = ParseAccountLinkSnapshot(responseJson);
            var entitlementSnapshot = NormalizeBridgeEntitlementSnapshot(ParseEntitlementSnapshot(responseJson));
            var signedToken = TryExtractSignedToken(responseJson) ?? string.Empty;

            if (!isSuccess)
            {
                if (parsedSnapshot is not null)
                {
                    return await PersistResultAsync(MergeSnapshot(parsedSnapshot, currentSnapshot), responseMessage, false, entitlementSnapshot, signedToken, cancellationToken);
                }

                return CreateFailureResult(responseMessage, currentSnapshot, FilterVisibleEntitlementSnapshot(GetStoredEntitlementSettings(), entitlementSnapshot));
            }

            if (parsedSnapshot is null && entitlementSnapshot is null)
            {
                return CreateFailureResult("Account link status response did not include account or entitlement data.", currentSnapshot);
            }

            var nextSnapshot = parsedSnapshot is null
                ? currentSnapshot
                : MergeSnapshot(parsedSnapshot, currentSnapshot);

            return await PersistResultAsync(nextSnapshot, responseMessage, true, entitlementSnapshot, signedToken, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return CreateFailureResult($"Account link refresh failed: {ex.Message}");
        }
    }

    public async Task<AccountLinkOperationResult> RestoreStorePurchaseAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var storeRuntimeContext = storeRuntimeContextProvider.GetCurrent();

        if (!storeRuntimeContext.IsStoreInstall)
        {
            return CreateFailureResult("Restore Store Purchase is only available on Microsoft Store installs.");
        }

        if (!options.IsConfigured)
        {
            return CreateFailureResult("Account linking is not configured. Set NWSHELPER_SUPABASE_URL and NWSHELPER_SUPABASE_ANON_KEY.");
        }

        try
        {
            var ownershipVerification = await storeOwnershipVerifier.VerifyAsync(cancellationToken);
            if (ownershipVerification.IsVerified)
            {
                if (!ownershipVerification.IsOwned || ownershipVerification.Evidence is null)
                {
                    return CreateFailureResult(ownershipVerification.Message);
                }

                storeRuntimeContext = storeRuntimeContext.WithVerifiedOwnership(ownershipVerification.Evidence);
            }

            var (isSuccess, responseJson, statusCode) = await SendRequestAsync(
                HttpMethod.Post,
                BuildUri(options.ClaimStorePath),
                new
                {
                    installationIdHash = ComputeInstallationHash(),
                    appVersion = AppVersionProvider.GetDisplayVersion(),
                    storeProof = storeRuntimeContext.CreateProofEnvelope()
                },
                cancellationToken);

            var responseMessage = TryExtractResponseMessage(responseJson)
                ?? (isSuccess
                    ? "Store purchase restore completed."
                    : $"Store purchase restore failed with HTTP {statusCode}.");

            var currentSnapshot = GetSnapshot();
            var parsedSnapshot = ParseAccountLinkSnapshot(responseJson);
            var entitlementSnapshot = NormalizeBridgeEntitlementSnapshot(ParseEntitlementSnapshot(responseJson));
            var signedToken = TryExtractSignedToken(responseJson) ?? string.Empty;

            if (!isSuccess)
            {
                if (parsedSnapshot is not null)
                {
                    return await PersistResultAsync(MergeSnapshot(parsedSnapshot, currentSnapshot), responseMessage, false, entitlementSnapshot, signedToken, cancellationToken);
                }

                return CreateFailureResult(responseMessage, currentSnapshot, FilterVisibleEntitlementSnapshot(GetStoredEntitlementSettings(), entitlementSnapshot));
            }

            AccountLinkSnapshot nextSnapshot;
            if (parsedSnapshot is not null)
            {
                nextSnapshot = MergeSnapshot(parsedSnapshot, currentSnapshot);
            }
            else if (entitlementSnapshot?.HasUnlimitedAddressesAddOn == true && currentSnapshot.HasActiveSession)
            {
                nextSnapshot = new AccountLinkSnapshot
                {
                    Status = AccountLinkStateStatus.Linked,
                    AccountId = currentSnapshot.AccountId,
                    Email = currentSnapshot.Email,
                    PurchaseSource = "store",
                    LinkedAtUtc = currentSnapshot.LinkedAtUtc ?? DateTimeOffset.UtcNow,
                    LastSyncUtc = DateTimeOffset.UtcNow,
                    LastError = string.Empty
                };
            }
            else
            {
                nextSnapshot = currentSnapshot;
            }

            return await PersistResultAsync(nextSnapshot, responseMessage, true, entitlementSnapshot, signedToken, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return CreateFailureResult($"Store purchase restore failed: {ex.Message}");
        }
    }

    private async Task<AccountLinkOperationResult> PersistResultAsync(
        AccountLinkSnapshot snapshot,
        string message,
        bool isSuccess,
        EntitlementSnapshot? entitlementSnapshot,
        string signedToken,
        CancellationToken cancellationToken)
    {
        var persistenceResult = await PersistSnapshotAsync(snapshot, entitlementSnapshot, signedToken, cancellationToken);
        if (!persistenceResult.IsSuccess)
        {
            return new AccountLinkOperationResult
            {
                IsSuccess = false,
                Message = persistenceResult.Message,
                Snapshot = persistenceResult.Snapshot,
                EntitlementSnapshot = persistenceResult.EntitlementSnapshot
            };
        }

        return new AccountLinkOperationResult
        {
            IsSuccess = isSuccess,
            Message = message,
            Snapshot = persistenceResult.Snapshot,
            EntitlementSnapshot = persistenceResult.EntitlementSnapshot
        };
    }

    private AccountLinkOperationResult CreateFailureResult(
        string message,
        AccountLinkSnapshot? snapshot = null,
        EntitlementSnapshot? entitlementSnapshot = null)
    {
        return new AccountLinkOperationResult
        {
            IsSuccess = false,
            Message = message,
            Snapshot = snapshot ?? GetSnapshot(),
            EntitlementSnapshot = entitlementSnapshot
        };
    }

    private async Task<(bool IsSuccess, string ResponseJson, int StatusCode)> SendRequestAsync(
        HttpMethod method,
        string requestUri,
        object? payload,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, requestUri);
        request.Headers.TryAddWithoutValidation("apikey", options.SupabaseAnonKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.SupabaseAnonKey);

        if (payload is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(payload, RequestJsonSerializerOptions), Encoding.UTF8, "application/json");
        }

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        return (response.IsSuccessStatusCode, responseJson, (int)response.StatusCode);
    }

    private string BuildUri(string path)
    {
        return $"{options.SupabaseUrl.TrimEnd('/')}/{path.TrimStart('/')}";
    }

    private string BuildUriWithQuery(string path, string installationIdHash, string appVersion)
    {
        var builder = new UriBuilder(BuildUri(path))
        {
            Query = $"installationIdHash={Uri.EscapeDataString(installationIdHash)}&appVersion={Uri.EscapeDataString(appVersion)}"
        };

        return builder.Uri.ToString();
    }

    private static string ComputeInstallationHash()
    {
        var payload = $"{Environment.MachineName}|{Environment.UserName}|{Environment.OSVersion.VersionString}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(bytes);
    }

    private static AccountLinkSnapshot MergeSnapshot(AccountLinkSnapshot snapshot, AccountLinkSnapshot currentSnapshot, string? emailOverride = null)
    {
        return new AccountLinkSnapshot
        {
            Status = snapshot.Status,
            AccountId = !string.IsNullOrWhiteSpace(snapshot.AccountId) ? snapshot.AccountId : currentSnapshot.AccountId,
            Email = !string.IsNullOrWhiteSpace(snapshot.Email)
                ? snapshot.Email
                : !string.IsNullOrWhiteSpace(emailOverride)
                    ? emailOverride
                    : currentSnapshot.Email,
            PurchaseSource = !string.IsNullOrWhiteSpace(snapshot.PurchaseSource) ? snapshot.PurchaseSource : currentSnapshot.PurchaseSource,
            LinkedAtUtc = snapshot.LinkedAtUtc ?? currentSnapshot.LinkedAtUtc,
            LastSyncUtc = snapshot.LastSyncUtc ?? DateTimeOffset.UtcNow,
            LastError = snapshot.LastError
        };
    }

    private static AccountLinkSnapshot? ParseAccountLinkSnapshot(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (TryGetProperty(root, "accountLink", out var nestedAccountLink) && nestedAccountLink.ValueKind == JsonValueKind.Object)
            {
                root = nestedAccountLink;
            }
            else if (TryGetProperty(root, "account_link", out var snakeAccountLink) && snakeAccountLink.ValueKind == JsonValueKind.Object)
            {
                root = snakeAccountLink;
            }
            else if (!HasAccountLinkPayload(root))
            {
                return null;
            }

            return new AccountLinkSnapshot
            {
                Status = ParseStatus(ReadString(root, "status")),
                AccountId = ReadString(root, "accountId") ?? ReadString(root, "account_id") ?? string.Empty,
                Email = ReadString(root, "email") ?? string.Empty,
                PurchaseSource = ReadString(root, "purchaseSource") ?? ReadString(root, "purchase_source") ?? string.Empty,
                LinkedAtUtc = ReadNullableDateTimeOffset(root, "linkedAtUtc") ?? ReadNullableDateTimeOffset(root, "linked_at_utc"),
                LastSyncUtc = ReadNullableDateTimeOffset(root, "lastSyncUtc") ?? ReadNullableDateTimeOffset(root, "last_sync_utc"),
                LastError = ReadString(root, "lastError") ?? ReadString(root, "last_error") ?? string.Empty
            };
        }
        catch
        {
            return null;
        }
    }

    private static bool HasAccountLinkPayload(JsonElement root)
    {
        return TryGetProperty(root, "status", out _) ||
               TryGetProperty(root, "accountId", out _) ||
               TryGetProperty(root, "account_id", out _) ||
               TryGetProperty(root, "email", out _) ||
               TryGetProperty(root, "purchaseSource", out _) ||
               TryGetProperty(root, "purchase_source", out _);
    }

    private static AccountLinkStateStatus ParseStatus(string? rawValue)
    {
        var normalized = (rawValue ?? string.Empty)
            .Trim()
            .Replace("_", string.Empty)
            .Replace("-", string.Empty)
            .ToLowerInvariant();

        return normalized switch
        {
            "awaitingconfirmation" or "emailsent" or "pendingsignin" or "checkemail" => AccountLinkStateStatus.AwaitingConfirmation,
            "signedin" => AccountLinkStateStatus.SignedIn,
            "linked" => AccountLinkStateStatus.Linked,
            "pendingreview" or "needsreview" or "manualreview" => AccountLinkStateStatus.PendingReview,
            "failed" or "actionrequired" => AccountLinkStateStatus.Failed,
            _ => AccountLinkStateStatus.SignedOut
        };
    }

    private static EntitlementSnapshot? ParseEntitlementSnapshot(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (TryGetProperty(root, "entitlement", out var nestedEntitlement) && nestedEntitlement.ValueKind == JsonValueKind.Object)
            {
                root = nestedEntitlement;
            }

            if (!TryGetProperty(root, "maxNewAddressesPerTerritory", out _) &&
                !TryGetProperty(root, "max_new_addresses_per_territory", out _) &&
                !TryGetProperty(root, "basePlanCode", out _) &&
                !TryGetProperty(root, "base_plan_code", out _) &&
                !TryGetProperty(root, "addOnCodes", out _) &&
                !TryGetProperty(root, "add_on_codes", out _) &&
                !TryGetProperty(root, "addOns", out _) &&
                !TryGetProperty(root, "add_ons", out _))
            {
                return null;
            }

            var basePlanCode = ReadString(root, "basePlanCode")
                ?? ReadString(root, "base_plan_code")
                ?? ReadString(root, "basePlan")
                ?? ReadString(root, "base_plan")
                ?? EntitlementProductCodes.FreeBasePlan;

            var addOnCodes = ParseAddOnCodes(root);

            var maxNewAddresses = ReadNullableInt(root, "maxNewAddressesPerTerritory")
                ?? ReadNullableInt(root, "max_new_addresses_per_territory")
                ?? (HasUnlimitedAddressesAddOn(addOnCodes) ? null : DefaultEntitlementPolicyEvaluator.FreeTierDefaultMaxNewAddressesPerTerritory);

            return new EntitlementSnapshot
            {
                BasePlanCode = basePlanCode.Trim().ToLowerInvariant(),
                AddOnCodes = addOnCodes,
                MaxNewAddressesPerTerritory = maxNewAddresses,
                ExpiresUtc = ReadNullableDateTimeOffset(root, "expiresUtc") ?? ReadNullableDateTimeOffset(root, "expires_utc"),
                LastValidatedUtc = DateTimeOffset.UtcNow,
                ValidationSource = ReadString(root, "validationSource") ?? ReadString(root, "validation_source") ?? "AccountLink"
            };
        }
        catch
        {
            return null;
        }
    }

    private static EntitlementSnapshot? NormalizeBridgeEntitlementSnapshot(EntitlementSnapshot? entitlementSnapshot)
    {
        if (entitlementSnapshot is null)
        {
            return null;
        }

        return new EntitlementSnapshot
        {
            BasePlanCode = entitlementSnapshot.BasePlanCode,
            AddOnCodes = entitlementSnapshot.AddOnCodes,
            MaxNewAddressesPerTerritory = entitlementSnapshot.MaxNewAddressesPerTerritory,
            ExpiresUtc = entitlementSnapshot.ExpiresUtc,
            LastValidatedUtc = entitlementSnapshot.LastValidatedUtc,
            ValidationSource = string.IsNullOrWhiteSpace(entitlementSnapshot.ValidationSource) ||
                               string.Equals(entitlementSnapshot.ValidationSource, "Online", StringComparison.OrdinalIgnoreCase)
                ? "AccountLink"
                : entitlementSnapshot.ValidationSource
        };
    }

    private static string? TryExtractResponseMessage(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return ReadString(root, "message")
                ?? ReadString(root, "error")
                ?? ReadString(root, "detail");
        }
        catch
        {
            return null;
        }
    }

    private static string? TryExtractSignedToken(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return ReadString(root, "token")
                ?? ReadString(root, "signedToken")
                ?? ReadString(root, "signed_token");
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetProperty(JsonElement root, string propertyName, out JsonElement value)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? ReadString(JsonElement root, string propertyName)
    {
        if (!TryGetProperty(root, propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static int? ReadNullableInt(JsonElement root, string propertyName)
    {
        if (!TryGetProperty(root, propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var intValue))
        {
            return intValue;
        }

        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static DateTimeOffset? ReadNullableDateTimeOffset(JsonElement root, string propertyName)
    {
        if (!TryGetProperty(root, propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), out var parsed))
        {
            return parsed.ToUniversalTime();
        }

        return null;
    }

    private static AccountLinkSnapshot ToSnapshot(GuiAccountLinkSettings? settings)
    {
        if (settings is null)
        {
            return AccountLinkSnapshot.CreateSignedOut();
        }

        if (settings.Status == AccountLinkStateStatus.SignedOut &&
            string.IsNullOrWhiteSpace(settings.AccountId) &&
            string.IsNullOrWhiteSpace(settings.Email) &&
            string.IsNullOrWhiteSpace(settings.PurchaseSource) &&
            !settings.LinkedAtUtc.HasValue &&
            !settings.LastSyncUtc.HasValue &&
            string.IsNullOrWhiteSpace(settings.LastError))
        {
            return AccountLinkSnapshot.CreateSignedOut();
        }

        return new AccountLinkSnapshot
        {
            Status = settings.Status,
            AccountId = settings.AccountId ?? string.Empty,
            Email = settings.Email ?? string.Empty,
            PurchaseSource = settings.PurchaseSource ?? string.Empty,
            LinkedAtUtc = settings.LinkedAtUtc,
            LastSyncUtc = settings.LastSyncUtc,
            LastError = settings.LastError ?? string.Empty
        };
    }

    private static GuiAccountLinkSettings ToSettings(AccountLinkSnapshot snapshot)
    {
        return new GuiAccountLinkSettings
        {
            Status = snapshot.Status,
            AccountId = snapshot.AccountId ?? string.Empty,
            Email = snapshot.Email ?? string.Empty,
            PurchaseSource = snapshot.PurchaseSource ?? string.Empty,
            LinkedAtUtc = snapshot.LinkedAtUtc,
            LastSyncUtc = snapshot.LastSyncUtc,
            LastError = snapshot.LastError ?? string.Empty
        };
    }

    private static bool ShouldPersistBridgeEntitlement(GuiEntitlementSettings existingSettings, EntitlementSnapshot? entitlementSnapshot)
    {
        if (entitlementSnapshot is null)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(existingSettings.ActivationKey))
        {
            return true;
        }

        return ShouldBridgeOverrideExistingEntitlement(existingSettings, entitlementSnapshot);
    }

    private GuiEntitlementSettings? GetStoredEntitlementSettings()
    {
        try
        {
            return configurationStore.Load().Entitlement;
        }
        catch
        {
            return null;
        }
    }

    private static EntitlementSnapshot? FilterVisibleEntitlementSnapshot(
        GuiEntitlementSettings? existingSettings,
        EntitlementSnapshot? entitlementSnapshot)
    {
        if (entitlementSnapshot is null)
        {
            return null;
        }

        var currentSettings = existingSettings ?? new GuiEntitlementSettings();
        return ShouldPersistBridgeEntitlement(currentSettings, entitlementSnapshot)
            ? entitlementSnapshot
            : null;
    }

    private static bool ShouldBridgeOverrideExistingEntitlement(GuiEntitlementSettings existingSettings, EntitlementSnapshot bridgeEntitlement)
    {
        if (!bridgeEntitlement.HasUnlimitedAddressesAddOn)
        {
            return false;
        }

        var existingAddOnCodes = existingSettings.AddOnCodes ?? Array.Empty<string>();
        if (!HasUnlimitedAddressesAddOn(existingAddOnCodes))
        {
            return true;
        }

        return IsDowngradedEntitlementSource(existingSettings.ValidationSource);
    }

    private static bool IsDowngradedEntitlementSource(string? validationSource)
    {
        if (string.IsNullOrWhiteSpace(validationSource))
        {
            return false;
        }

        return validationSource.Contains("revoked", StringComparison.OrdinalIgnoreCase)
            || validationSource.Contains("invalid", StringComparison.OrdinalIgnoreCase)
            || validationSource.Contains("offline", StringComparison.OrdinalIgnoreCase)
            || validationSource.Contains("expired", StringComparison.OrdinalIgnoreCase)
            || validationSource.Contains("clockrollback", StringComparison.OrdinalIgnoreCase);
    }

    private static GuiEntitlementSettings CloneWithUpdatedAccountLink(GuiEntitlementSettings existingSettings, AccountLinkSnapshot snapshot)
    {
        return new GuiEntitlementSettings
        {
            ActivationKey = existingSettings.ActivationKey ?? string.Empty,
            ActivationKeyHash = existingSettings.ActivationKeyHash ?? string.Empty,
            BasePlanCode = existingSettings.BasePlanCode ?? EntitlementProductCodes.FreeBasePlan,
            AddOnCodes = existingSettings.AddOnCodes ?? Array.Empty<string>(),
            MaxNewAddressesPerTerritory = existingSettings.MaxNewAddressesPerTerritory,
            ExpiresUtc = existingSettings.ExpiresUtc,
            LastValidatedUtc = existingSettings.LastValidatedUtc,
            ValidationSource = existingSettings.ValidationSource ?? "LocalCache",
            SignedToken = existingSettings.SignedToken ?? string.Empty,
            AccountLink = ToSettings(snapshot)
        };
    }

    private static GuiEntitlementSettings MergeEntitlementSettings(
        GuiEntitlementSettings existingSettings,
        AccountLinkSnapshot snapshot,
        EntitlementSnapshot entitlementSnapshot,
        string signedToken)
    {
        return new GuiEntitlementSettings
        {
            ActivationKey = existingSettings.ActivationKey ?? string.Empty,
            ActivationKeyHash = existingSettings.ActivationKeyHash ?? string.Empty,
            BasePlanCode = entitlementSnapshot.BasePlanCode,
            AddOnCodes = entitlementSnapshot.AddOnCodes.ToArray(),
            MaxNewAddressesPerTerritory = entitlementSnapshot.MaxNewAddressesPerTerritory,
            ExpiresUtc = entitlementSnapshot.ExpiresUtc,
            LastValidatedUtc = entitlementSnapshot.LastValidatedUtc,
            ValidationSource = entitlementSnapshot.ValidationSource ?? "Online",
            SignedToken = entitlementSnapshot.HasUnlimitedAddressesAddOn
                ? (!string.IsNullOrWhiteSpace(signedToken) ? signedToken : existingSettings.SignedToken ?? string.Empty)
                : string.Empty,
            AccountLink = ToSettings(snapshot)
        };
    }

    private static string[] ParseAddOnCodes(JsonElement root)
    {
        if (!TryGetProperty(root, "addOnCodes", out var value) &&
            !TryGetProperty(root, "add_on_codes", out value) &&
            !TryGetProperty(root, "addOns", out value) &&
            !TryGetProperty(root, "add_ons", out value))
        {
            return Array.Empty<string>();
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            return value.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => (item.GetString() ?? string.Empty).Trim().ToLowerInvariant())
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString()!
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(code => code.Trim().ToLowerInvariant())
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        return Array.Empty<string>();
    }

    private static bool HasUnlimitedAddressesAddOn(IEnumerable<string> addOnCodes)
    {
        return addOnCodes.Any(code => string.Equals(code, EntitlementProductCodes.UnlimitedAddressesAddOn, StringComparison.OrdinalIgnoreCase));
    }
}