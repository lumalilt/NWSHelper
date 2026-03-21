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

public sealed class EntitlementSnapshot
{
    public string BasePlanCode { get; init; } = EntitlementProductCodes.FreeBasePlan;

    public IReadOnlyCollection<string> AddOnCodes { get; init; } = Array.Empty<string>();

    public int? MaxNewAddressesPerTerritory { get; init; } = DefaultEntitlementPolicyEvaluator.FreeTierDefaultMaxNewAddressesPerTerritory;

    public DateTimeOffset? ExpiresUtc { get; init; }

    public DateTimeOffset? LastValidatedUtc { get; init; }

    public string ValidationSource { get; init; } = "LocalCache";

    public bool IsExpired => ExpiresUtc.HasValue && ExpiresUtc.Value <= DateTimeOffset.UtcNow;

    public bool HasUnlimitedAddressesAddOn => !IsExpired && HasAddOn(EntitlementProductCodes.UnlimitedAddressesAddOn);

    public bool HasAddOn(string addOnCode)
    {
        return AddOnCodes.Any(code => string.Equals(code, addOnCode, StringComparison.OrdinalIgnoreCase));
    }

    public EntitlementContext ToCoreContext()
    {
        return new EntitlementContext
        {
            BasePlanCode = BasePlanCode,
            AddOnCodes = AddOnCodes
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Select(code => code.Trim().ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            MaxNewAddressesPerTerritory = MaxNewAddressesPerTerritory,
            ExpiresUtc = ExpiresUtc,
            LastValidatedUtc = LastValidatedUtc,
            ValidationSource = ValidationSource
        };
    }

    public static EntitlementSnapshot CreateDefaultFree(string source = "LocalDefault")
    {
        return new EntitlementSnapshot
        {
            BasePlanCode = EntitlementProductCodes.FreeBasePlan,
            AddOnCodes = Array.Empty<string>(),
            MaxNewAddressesPerTerritory = DefaultEntitlementPolicyEvaluator.FreeTierDefaultMaxNewAddressesPerTerritory,
            LastValidatedUtc = DateTimeOffset.UtcNow,
            ValidationSource = source
        };
    }

    public static EntitlementSnapshot CreateWithUnlimitedAddresses(string source = "LocalDefault", DateTimeOffset? expiresUtc = null)
    {
        return new EntitlementSnapshot
        {
            BasePlanCode = EntitlementProductCodes.FreeBasePlan,
            AddOnCodes = new[] { EntitlementProductCodes.UnlimitedAddressesAddOn },
            MaxNewAddressesPerTerritory = null,
            ExpiresUtc = expiresUtc,
            LastValidatedUtc = DateTimeOffset.UtcNow,
            ValidationSource = source
        };
    }
}

public sealed class EntitlementActivationResult
{
    public bool IsSuccess { get; init; }

    public string Message { get; init; } = string.Empty;

    public EntitlementSnapshot Snapshot { get; init; } = EntitlementSnapshot.CreateDefaultFree();
}

public sealed class GuiEntitlementSettings
{
    public string ActivationKey { get; set; } = string.Empty;

    public string ActivationKeyHash { get; set; } = string.Empty;

    public string BasePlanCode { get; set; } = EntitlementProductCodes.FreeBasePlan;

    public string[] AddOnCodes { get; set; } = Array.Empty<string>();

    public int? MaxNewAddressesPerTerritory { get; set; } = DefaultEntitlementPolicyEvaluator.FreeTierDefaultMaxNewAddressesPerTerritory;

    public DateTimeOffset? ExpiresUtc { get; set; }

    public DateTimeOffset? LastValidatedUtc { get; set; }

    public string ValidationSource { get; set; } = "LocalCache";

    public string SignedToken { get; set; } = string.Empty;

    public GuiAccountLinkSettings AccountLink { get; set; } = new();
}

public sealed class SupabaseActivationOptions
{
    public string SupabaseUrl { get; init; } = Environment.GetEnvironmentVariable("NWSHELPER_SUPABASE_URL") ?? string.Empty;

    public string SupabaseAnonKey { get; init; } = Environment.GetEnvironmentVariable("NWSHELPER_SUPABASE_ANON_KEY") ?? string.Empty;

    public string TokenSigningSecret { get; init; } = Environment.GetEnvironmentVariable("NWSHELPER_TOKEN_SIGNING_SECRET") ?? string.Empty;

    public string ActivateFunctionPath { get; init; } = "functions/v1/activate-license";

    public TimeSpan OfflineGracePeriod { get; init; } = ReadDurationFromEnvironment(
        variableName: "NWSHELPER_ENTITLEMENT_OFFLINE_GRACE_HOURS",
        defaultValue: TimeSpan.FromDays(14),
        conversion: static hours => TimeSpan.FromHours(hours));

    public TimeSpan OnlineRefreshInterval { get; init; } = ReadDurationFromEnvironment(
        variableName: "NWSHELPER_ENTITLEMENT_REFRESH_HOURS",
        defaultValue: TimeSpan.FromHours(24),
        conversion: static hours => TimeSpan.FromHours(hours));

    public TimeSpan ClockRollbackTolerance { get; init; } = ReadDurationFromEnvironment(
        variableName: "NWSHELPER_ENTITLEMENT_CLOCK_SKEW_MINUTES",
        defaultValue: TimeSpan.FromMinutes(10),
        conversion: static minutes => TimeSpan.FromMinutes(minutes));

    public bool IsConfigured => !string.IsNullOrWhiteSpace(SupabaseUrl) && !string.IsNullOrWhiteSpace(SupabaseAnonKey);

    public bool HasTokenSigningSecret => !string.IsNullOrWhiteSpace(TokenSigningSecret);

    private static TimeSpan ReadDurationFromEnvironment(string variableName, TimeSpan defaultValue, Func<double, TimeSpan> conversion)
    {
        var value = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        if (!double.TryParse(value, out var parsed) || parsed <= 0)
        {
            return defaultValue;
        }

        try
        {
            return conversion(parsed);
        }
        catch
        {
            return defaultValue;
        }
    }
}

public interface IEntitlementService
{
    EntitlementSnapshot GetSnapshot();

    Task<EntitlementSnapshot> RefreshAsync(CancellationToken cancellationToken, bool forceOnlineRevalidation = false);

    Task<EntitlementActivationResult> ActivateAsync(string activationKey, CancellationToken cancellationToken);
}

public sealed class SupabaseEntitlementService : IEntitlementService
{
    private readonly GuiConfigurationStore configurationStore;
    private readonly HttpClient httpClient;
    private readonly SupabaseActivationOptions options;

    public SupabaseEntitlementService(
        string? filePath = null,
        HttpClient? httpClient = null,
        SupabaseActivationOptions? options = null)
    {
        configurationStore = new GuiConfigurationStore(filePath);
        this.httpClient = httpClient ?? new HttpClient();
        this.options = options ?? new SupabaseActivationOptions();
    }

    public EntitlementSnapshot GetSnapshot()
    {
        try
        {
            var settings = configurationStore.Load().Entitlement;
            if (settings is null)
            {
                return EntitlementSnapshot.CreateDefaultFree();
            }

            return EvaluateCachedSnapshot(settings, persistDowngrades: true);
        }
        catch
        {
            return EntitlementSnapshot.CreateDefaultFree("LoadFailed");
        }
    }

    public async Task<EntitlementSnapshot> RefreshAsync(CancellationToken cancellationToken, bool forceOnlineRevalidation = false)
    {
        cancellationToken.ThrowIfCancellationRequested();

        GuiEntitlementSettings? settings;
        try
        {
            settings = configurationStore.Load().Entitlement;
        }
        catch
        {
            return EntitlementSnapshot.CreateDefaultFree("LoadFailed");
        }

        if (settings is null)
        {
            return EntitlementSnapshot.CreateDefaultFree();
        }

        var snapshot = EvaluateCachedSnapshot(settings, persistDowngrades: true);
        if (!ShouldAttemptOnlineRevalidation(settings, snapshot, forceOnlineRevalidation))
        {
            return snapshot;
        }

        var activationResult = await ActivateAsync(settings.ActivationKey, cancellationToken);
        if (activationResult.IsSuccess)
        {
            return activationResult.Snapshot;
        }

        if (ShouldDowngradeForRevocationMessage(activationResult.Message))
        {
            var downgraded = EntitlementSnapshot.CreateDefaultFree("RevokedOnline");
            SaveSnapshot(downgraded, settings.ActivationKeyHash, settings.SignedToken, settings.ActivationKey);
            return downgraded;
        }

        return GetSnapshot();
    }

    public async Task<EntitlementActivationResult> ActivateAsync(string activationKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(activationKey))
        {
            return new EntitlementActivationResult
            {
                IsSuccess = false,
                Message = "Enter an activation key before validating.",
                Snapshot = GetSnapshot()
            };
        }

        if (!options.IsConfigured)
        {
            return new EntitlementActivationResult
            {
                IsSuccess = false,
                Message = "Supabase activation is not configured. Set NWSHELPER_SUPABASE_URL and NWSHELPER_SUPABASE_ANON_KEY.",
                Snapshot = EntitlementSnapshot.CreateDefaultFree("SupabaseNotConfigured")
            };
        }

        try
        {
            var payload = new
            {
                licenseKey = activationKey.Trim(),
                installationIdHash = ComputeInstallationHash(),
                appVersion = AppVersionProvider.GetDisplayVersion()
            };

            var requestUri = BuildActivationUri();
            using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };

            request.Headers.TryAddWithoutValidation("apikey", options.SupabaseAnonKey);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.SupabaseAnonKey);

            using var response = await httpClient.SendAsync(request, cancellationToken);
            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var message = TryExtractResponseMessage(responseJson)
                    ?? $"Activation failed with HTTP {(int)response.StatusCode}.";

                if (ShouldDowngradeForRevocationMessage(message))
                {
                    SaveDowngradedSnapshot("RevokedOnline");
                }

                return new EntitlementActivationResult
                {
                    IsSuccess = false,
                    Message = message,
                    Snapshot = GetSnapshot()
                };
            }

            var snapshot = ParseActivationSnapshot(responseJson);
            var keyHash = ComputeActivationKeyHash(activationKey);
            var token = TryExtractSignedToken(responseJson) ?? string.Empty;

            if (!TryValidateActivationResponseToken(snapshot, token, out var tokenValidationMessage))
            {
                var downgraded = EntitlementSnapshot.CreateDefaultFree("InvalidTokenSignature");
                SaveSnapshot(downgraded, keyHash, string.Empty, activationKey.Trim());
                return new EntitlementActivationResult
                {
                    IsSuccess = false,
                    Message = tokenValidationMessage,
                    Snapshot = downgraded
                };
            }

            SaveSnapshot(snapshot, keyHash, token, activationKey.Trim());

            var successMessage = snapshot.HasUnlimitedAddressesAddOn
                ? "Product add-on validated. Unlimited new addresses per territory is active."
                : "Activation validated, but the entitlement is currently limited.";

            return new EntitlementActivationResult
            {
                IsSuccess = true,
                Message = successMessage,
                Snapshot = snapshot
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new EntitlementActivationResult
            {
                IsSuccess = false,
                Message = $"Activation request failed: {ex.Message}",
                Snapshot = GetSnapshot()
            };
        }
    }

    private string BuildActivationUri()
    {
        var baseUrl = options.SupabaseUrl.TrimEnd('/');
        var path = options.ActivateFunctionPath.TrimStart('/');
        return $"{baseUrl}/{path}";
    }

    private EntitlementSnapshot ParseActivationSnapshot(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (TryGetProperty(root, "entitlement", out var nestedEntitlement) && nestedEntitlement.ValueKind == JsonValueKind.Object)
        {
            root = nestedEntitlement;
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

        var expiresUtc = ReadNullableDateTimeOffset(root, "expiresUtc")
            ?? ReadNullableDateTimeOffset(root, "expires_utc");

        var validationSource = ReadString(root, "validationSource")
            ?? ReadString(root, "validation_source")
            ?? "Online";

        return new EntitlementSnapshot
        {
            BasePlanCode = basePlanCode.Trim().ToLowerInvariant(),
            AddOnCodes = addOnCodes,
            MaxNewAddressesPerTerritory = maxNewAddresses,
            ExpiresUtc = expiresUtc,
            LastValidatedUtc = DateTimeOffset.UtcNow,
            ValidationSource = validationSource
        };
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

    private static string? TryExtractSignedToken(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        return ReadString(root, "token")
            ?? ReadString(root, "signedToken")
            ?? ReadString(root, "signed_token");
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

    private static string ComputeActivationKeyHash(string activationKey)
    {
        var bytes = Encoding.UTF8.GetBytes(activationKey.Trim());
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    private static string ComputeInstallationHash()
    {
        var machine = Environment.MachineName;
        var user = Environment.UserName;
        var os = Environment.OSVersion.VersionString;
        var payload = $"{machine}|{user}|{os}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash);
    }

    private EntitlementSnapshot ToSnapshot(GuiEntitlementSettings settings)
    {
        return new EntitlementSnapshot
        {
            BasePlanCode = string.IsNullOrWhiteSpace(settings.BasePlanCode)
                ? EntitlementProductCodes.FreeBasePlan
                : settings.BasePlanCode,
            AddOnCodes = NormalizeAddOnCodes(settings.AddOnCodes),
            MaxNewAddressesPerTerritory = settings.MaxNewAddressesPerTerritory,
            ExpiresUtc = settings.ExpiresUtc,
            LastValidatedUtc = settings.LastValidatedUtc,
            ValidationSource = settings.ValidationSource
        };
    }

    private EntitlementSnapshot EvaluateCachedSnapshot(GuiEntitlementSettings settings, bool persistDowngrades)
    {
        var snapshot = ToSnapshot(settings);
        if (!snapshot.HasUnlimitedAddressesAddOn)
        {
            return snapshot;
        }

        var now = DateTimeOffset.UtcNow;

        if (settings.LastValidatedUtc.HasValue && settings.LastValidatedUtc.Value > now.Add(options.ClockRollbackTolerance))
        {
            return DowngradeSnapshot("ClockRollbackDetected", settings, persistDowngrades);
        }

        if (options.HasTokenSigningSecret)
        {
            if (!TryValidateSignedToken(settings.SignedToken, snapshot, out var tokenFailureReason))
            {
                return DowngradeSnapshot(tokenFailureReason, settings, persistDowngrades);
            }
        }

        if (!settings.LastValidatedUtc.HasValue || settings.LastValidatedUtc.Value.Add(options.OfflineGracePeriod) < now)
        {
            return DowngradeSnapshot("OfflineGraceExpired", settings, persistDowngrades);
        }

        if (snapshot.IsExpired)
        {
            return DowngradeSnapshot("ExpiredCache", settings, persistDowngrades);
        }

        return snapshot;
    }

    private EntitlementSnapshot DowngradeSnapshot(string source, GuiEntitlementSettings settings, bool persist)
    {
        var downgraded = EntitlementSnapshot.CreateDefaultFree(source);
        if (persist)
        {
            SaveSnapshot(downgraded, settings.ActivationKeyHash, settings.SignedToken, settings.ActivationKey);
        }

        return downgraded;
    }

    private bool ShouldAttemptOnlineRevalidation(GuiEntitlementSettings settings, EntitlementSnapshot snapshot, bool forceOnlineRevalidation)
    {
        if (!options.IsConfigured)
        {
            return false;
        }

        if (!snapshot.HasUnlimitedAddressesAddOn)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(settings.ActivationKey))
        {
            return false;
        }

        if (forceOnlineRevalidation)
        {
            return true;
        }

        if (!settings.LastValidatedUtc.HasValue)
        {
            return true;
        }

        return settings.LastValidatedUtc.Value.Add(options.OnlineRefreshInterval) <= DateTimeOffset.UtcNow;
    }

    private bool TryValidateActivationResponseToken(EntitlementSnapshot snapshot, string signedToken, out string message)
    {
        if (!options.HasTokenSigningSecret)
        {
            message = string.Empty;
            return true;
        }

        if (string.IsNullOrWhiteSpace(signedToken))
        {
            message = "Activation response did not include a signed entitlement token.";
            return false;
        }

        var isValid = TryValidateSignedToken(signedToken, snapshot, out var failureReason);
        message = isValid
            ? string.Empty
            : $"Activation response token verification failed ({failureReason}).";
        return isValid;
    }

    private bool TryValidateSignedToken(string signedToken, EntitlementSnapshot snapshot, out string failureReason)
    {
        failureReason = "InvalidTokenSignature";

        if (string.IsNullOrWhiteSpace(signedToken) || string.IsNullOrWhiteSpace(options.TokenSigningSecret))
        {
            return false;
        }

        var parts = signedToken.Split('.');
        if (parts.Length != 3)
        {
            return false;
        }

        var signingInput = $"{parts[0]}.{parts[1]}";
        var expectedSignatureBytes = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(options.TokenSigningSecret),
            Encoding.UTF8.GetBytes(signingInput));

        byte[] providedSignatureBytes;
        try
        {
            providedSignatureBytes = Base64UrlDecode(parts[2]);
        }
        catch
        {
            return false;
        }

        if (expectedSignatureBytes.Length != providedSignatureBytes.Length ||
            !CryptographicOperations.FixedTimeEquals(expectedSignatureBytes, providedSignatureBytes))
        {
            return false;
        }

        TokenPayload tokenPayload;
        try
        {
            tokenPayload = ParseTokenPayload(parts[1]);
        }
        catch
        {
            return false;
        }

        if (!string.Equals(tokenPayload.BasePlanCode, snapshot.BasePlanCode, StringComparison.OrdinalIgnoreCase))
        {
            failureReason = "TokenPayloadMismatch";
            return false;
        }

        if (!AreEquivalentAddOnSets(tokenPayload.AddOnCodes, snapshot.AddOnCodes))
        {
            failureReason = "TokenPayloadMismatch";
            return false;
        }

        if (tokenPayload.MaxNewAddressesPerTerritory != snapshot.MaxNewAddressesPerTerritory)
        {
            failureReason = "TokenPayloadMismatch";
            return false;
        }

        if (!AreEquivalentExpiration(tokenPayload.ExpiresUtc, snapshot.ExpiresUtc))
        {
            failureReason = "TokenPayloadMismatch";
            return false;
        }

        if (!string.Equals(tokenPayload.InstallationIdHash, ComputeInstallationHash(), StringComparison.OrdinalIgnoreCase))
        {
            failureReason = "InstallationMismatch";
            return false;
        }

        return true;
    }

    private static bool ShouldDowngradeForRevocationMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        return message.Contains("not active", StringComparison.OrdinalIgnoreCase)
            || message.Contains("revoked", StringComparison.OrdinalIgnoreCase)
            || message.Contains("expired", StringComparison.OrdinalIgnoreCase)
            || message.Contains("not found", StringComparison.OrdinalIgnoreCase);
    }

    private static bool AreEquivalentExpiration(DateTimeOffset? left, DateTimeOffset? right)
    {
        if (!left.HasValue && !right.HasValue)
        {
            return true;
        }

        if (!left.HasValue || !right.HasValue)
        {
            return false;
        }

        var delta = (left.Value - right.Value).Duration();
        return delta <= TimeSpan.FromSeconds(1);
    }

    private static TokenPayload ParseTokenPayload(string encodedPayload)
    {
        var payloadBytes = Base64UrlDecode(encodedPayload);
        using var document = JsonDocument.Parse(payloadBytes);
        var root = document.RootElement;

        return new TokenPayload
        {
            BasePlanCode = ReadString(root, "basePlanCode")
                ?? ReadString(root, "base_plan_code")
                ?? ReadString(root, "basePlan")
                ?? ReadString(root, "base_plan")
                ?? EntitlementProductCodes.FreeBasePlan,
            AddOnCodes = ParseAddOnCodes(root),
            MaxNewAddressesPerTerritory = ReadNullableInt(root, "maxNewAddressesPerTerritory")
                ?? ReadNullableInt(root, "max_new_addresses_per_territory"),
            ExpiresUtc = ReadNullableDateTimeOffset(root, "expiresUtc")
                ?? ReadNullableDateTimeOffset(root, "expires_utc"),
            InstallationIdHash = ReadString(root, "installationIdHash")
                ?? ReadString(root, "installation_id_hash")
                ?? string.Empty
        };
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        switch (normalized.Length % 4)
        {
            case 0:
                break;
            case 2:
                normalized += "==";
                break;
            case 3:
                normalized += "=";
                break;
            default:
                throw new FormatException("Invalid base64url value.");
        }

        return Convert.FromBase64String(normalized);
    }

    private void SaveDowngradedSnapshot(string source)
    {
        var settings = configurationStore.Load().Entitlement;
        if (settings is null)
        {
            return;
        }

        var downgraded = EntitlementSnapshot.CreateDefaultFree(source);
        SaveSnapshot(downgraded, settings.ActivationKeyHash, settings.SignedToken, settings.ActivationKey);
    }

    private void SaveSnapshot(EntitlementSnapshot snapshot, string activationKeyHash, string signedToken, string activationKey)
    {
        var document = configurationStore.Load();
        var existingSettings = document.Entitlement;
        document.Entitlement = new GuiEntitlementSettings
        {
            ActivationKey = activationKey,
            ActivationKeyHash = activationKeyHash,
            BasePlanCode = snapshot.BasePlanCode,
            AddOnCodes = NormalizeAddOnCodes(snapshot.AddOnCodes),
            MaxNewAddressesPerTerritory = snapshot.MaxNewAddressesPerTerritory,
            ExpiresUtc = snapshot.ExpiresUtc,
            LastValidatedUtc = snapshot.LastValidatedUtc,
            ValidationSource = snapshot.ValidationSource,
            SignedToken = signedToken,
            AccountLink = existingSettings?.AccountLink ?? new GuiAccountLinkSettings()
        };
        configurationStore.Save(document);
    }

    private sealed class TokenPayload
    {
        public string BasePlanCode { get; init; } = EntitlementProductCodes.FreeBasePlan;

        public string[] AddOnCodes { get; init; } = Array.Empty<string>();

        public int? MaxNewAddressesPerTerritory { get; init; }

        public DateTimeOffset? ExpiresUtc { get; init; }

        public string InstallationIdHash { get; init; } = string.Empty;
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
            return NormalizeAddOnCodes(value.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString() ?? string.Empty));
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return NormalizeAddOnCodes(value.GetString()?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                ?? Array.Empty<string>());
        }

        return Array.Empty<string>();
    }

    private static bool HasUnlimitedAddressesAddOn(IEnumerable<string> addOnCodes)
    {
        return addOnCodes.Any(code => string.Equals(code, EntitlementProductCodes.UnlimitedAddressesAddOn, StringComparison.OrdinalIgnoreCase));
    }

    private static string[] NormalizeAddOnCodes(IEnumerable<string>? addOnCodes)
    {
        return (addOnCodes ?? Array.Empty<string>())
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool AreEquivalentAddOnSets(IEnumerable<string> left, IEnumerable<string> right)
    {
        var leftSet = new HashSet<string>(NormalizeAddOnCodes(left), StringComparer.OrdinalIgnoreCase);
        var rightSet = new HashSet<string>(NormalizeAddOnCodes(right), StringComparer.OrdinalIgnoreCase);
        return leftSet.SetEquals(rightSet);
    }
}
