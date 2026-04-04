using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NWSHelper.Gui.Services;

public sealed class SupportDiagnosticsSnapshot
{
    public string CurrentVersion { get; init; } = string.Empty;

    public bool IsStoreInstall { get; init; }

    public string StatusMessage { get; init; } = string.Empty;

    public string LastError { get; init; } = string.Empty;

    public string AccountLinkStatusMessage { get; init; } = string.Empty;

    public AccountLinkSnapshot AccountLinkSnapshot { get; init; } = AccountLinkSnapshot.CreateSignedOut();

    public EntitlementSnapshot EntitlementSnapshot { get; init; } = EntitlementSnapshot.CreateDefaultFree();

    public bool HasStoreContinuityPrompt { get; init; }

    public string StoreContinuityPromptTitle { get; init; } = string.Empty;

    public string StoreContinuityPromptMessage { get; init; } = string.Empty;

    public bool CanRestoreStorePurchase { get; init; }

    public string StoreAddOnCatalogMessage { get; init; } = string.Empty;
}

public sealed class SupportDiagnosticsExportResult
{
    public bool IsSuccess { get; init; }

    public string Message { get; init; } = string.Empty;
}

public interface ISupportDiagnosticsExportService
{
    Task<SupportDiagnosticsExportResult> ExportAsync(string path, SupportDiagnosticsSnapshot snapshot, CancellationToken cancellationToken);
}

public sealed class SupportDiagnosticsExportService : ISupportDiagnosticsExportService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly GuiConfigurationStore configurationStore;
    private readonly IStoreRuntimeContextProvider storeRuntimeContextProvider;
    private readonly IStoreOwnershipVerifier storeOwnershipVerifier;
    private readonly Func<StoreOwnershipOptions> storeOwnershipOptionsAccessor;

    public SupportDiagnosticsExportService(
        string? filePath = null,
        IStoreRuntimeContextProvider? storeRuntimeContextProvider = null,
        IStoreOwnershipVerifier? storeOwnershipVerifier = null,
        Func<StoreOwnershipOptions>? storeOwnershipOptionsAccessor = null)
    {
        configurationStore = new GuiConfigurationStore(filePath);
        this.storeRuntimeContextProvider = storeRuntimeContextProvider ?? new StoreRuntimeContextProvider();
        this.storeOwnershipVerifier = storeOwnershipVerifier ?? StoreOwnershipVerifierFactory.CreateDefault();
        this.storeOwnershipOptionsAccessor = storeOwnershipOptionsAccessor ?? (() => new StoreOwnershipOptions());
    }

    public async Task<SupportDiagnosticsExportResult> ExportAsync(string path, SupportDiagnosticsSnapshot snapshot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(path))
        {
            return CreateFailureResult("Choose a file path for the support diagnostics export.");
        }

        try
        {
            var exportDirectory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(exportDirectory))
            {
                Directory.CreateDirectory(exportDirectory);
            }

            var configuration = configurationStore.Load();
            var storeRuntimeContext = storeRuntimeContextProvider.GetCurrent();
            var storeOwnershipOptions = storeOwnershipOptionsAccessor();
            var ownershipVerification = await storeOwnershipVerifier.VerifyAsync(cancellationToken);
            var effectiveStoreRuntimeContext = ownershipVerification is { IsVerified: true, Evidence: not null }
                ? storeRuntimeContext.WithVerifiedOwnership(ownershipVerification.Evidence)
                : storeRuntimeContext;
            var proofEnvelope = effectiveStoreRuntimeContext.CreateProofEnvelope();

            var document = new
            {
                schemaVersion = 1,
                diagnosticKind = "support-diagnostics",
                exportedAtUtc = DateTimeOffset.UtcNow,
                application = new
                {
                    snapshot.CurrentVersion,
                    snapshot.IsStoreInstall,
                    osVersion = Environment.OSVersion.VersionString,
                    Environment.Is64BitOperatingSystem,
                    Environment.Is64BitProcess
                },
                uiState = new
                {
                    snapshot.StatusMessage,
                    snapshot.LastError,
                    snapshot.AccountLinkStatusMessage,
                    snapshot.HasStoreContinuityPrompt,
                    snapshot.StoreContinuityPromptTitle,
                    snapshot.StoreContinuityPromptMessage,
                    snapshot.CanRestoreStorePurchase,
                    snapshot.StoreAddOnCatalogMessage
                },
                accountLink = new
                {
                    status = snapshot.AccountLinkSnapshot.Status.ToString(),
                    snapshot.AccountLinkSnapshot.AccountId,
                    snapshot.AccountLinkSnapshot.Email,
                    snapshot.AccountLinkSnapshot.PurchaseSource,
                    snapshot.AccountLinkSnapshot.LinkedAtUtc,
                    snapshot.AccountLinkSnapshot.LastSyncUtc,
                    snapshot.AccountLinkSnapshot.LastError
                },
                entitlement = new
                {
                    snapshot.EntitlementSnapshot.BasePlanCode,
                    snapshot.EntitlementSnapshot.AddOnCodes,
                    snapshot.EntitlementSnapshot.MaxNewAddressesPerTerritory,
                    snapshot.EntitlementSnapshot.ExpiresUtc,
                    snapshot.EntitlementSnapshot.LastValidatedUtc,
                    snapshot.EntitlementSnapshot.ValidationSource,
                    snapshot.EntitlementSnapshot.HasUnlimitedAddressesAddOn
                },
                persistedConfiguration = new
                {
                    theme = configuration.Theme.ToString(),
                    setup = configuration.Setup is null
                        ? null
                        : new
                        {
                            configuration.Setup.SelectedDatasetProviderId,
                            configuration.Setup.SelectedDatasetSourcesCsv,
                            configuration.Setup.OpenAddressesApiBaseUrl,
                            openAddressesApiTokenConfigured = !string.IsNullOrWhiteSpace(configuration.Setup.OpenAddressesApiToken),
                            configuration.Setup.UseEmbeddedOpenAddressesOnboardingWebView,
                            configuration.Setup.EnableOpenAddressesAdvancedDiagnostics,
                            configuration.Setup.OpenAddressesApiOnboardingType,
                            configuration.Setup.LastOpenAddressesOnboardingSuccessSummary,
                            configuration.Setup.BoundaryCsvPath,
                            configuration.Setup.ExistingAddressesCsvPath,
                            configuration.Setup.DatasetRootPath,
                            configuration.Setup.StatesFilter
                        },
                    entitlement = configuration.Entitlement is null
                        ? null
                        : new
                        {
                            configuration.Entitlement.BasePlanCode,
                            configuration.Entitlement.AddOnCodes,
                            configuration.Entitlement.MaxNewAddressesPerTerritory,
                            configuration.Entitlement.ExpiresUtc,
                            configuration.Entitlement.LastValidatedUtc,
                            configuration.Entitlement.ValidationSource,
                            activationKeyPresent = !string.IsNullOrWhiteSpace(configuration.Entitlement.ActivationKey),
                            activationKeyHashPresent = !string.IsNullOrWhiteSpace(configuration.Entitlement.ActivationKeyHash),
                            signedTokenPresent = !string.IsNullOrWhiteSpace(configuration.Entitlement.SignedToken)
                        },
                    updates = configuration.Updates is null
                        ? null
                        : new
                        {
                            configuration.Updates.AutoUpdateEnabled,
                            configuration.Updates.AppcastUrl,
                            appcastPublicKeyConfigured = !string.IsNullOrWhiteSpace(configuration.Updates.AppcastPublicKey),
                            configuration.Updates.LastCheckedUtc,
                            configuration.Updates.LastCheckStatus
                        }
                },
                storeRuntime = new
                {
                    effectiveStoreRuntimeContext.IsPackaged,
                    effectiveStoreRuntimeContext.IsStoreInstall,
                    proofAuthority = effectiveStoreRuntimeContext.ProofAuthority.ToString(),
                    effectiveStoreRuntimeContext.DetectionSource,
                    effectiveStoreRuntimeContext.PackageFamilyName,
                    effectiveStoreRuntimeContext.ProcessPath,
                    effectiveStoreRuntimeContext.CapturedAtUtc,
                    proofEnvelope = CreateSanitizedProofEnvelope(proofEnvelope)
                },
                storeOwnershipConfiguration = CreateSanitizedStoreOwnershipConfiguration(storeOwnershipOptions),
                storeOwnershipVerification = new
                {
                    ownershipVerification.IsVerified,
                    ownershipVerification.IsOwned,
                    ownershipVerification.IsTrial,
                    ownershipVerification.AllowHeuristicFallback,
                    ownershipVerification.Message,
                    evidence = CreateSanitizedStoreOwnershipEvidence(ownershipVerification.Evidence)
                }
            };

            WriteJsonAtomically(path, document);

            return new SupportDiagnosticsExportResult
            {
                IsSuccess = true,
                Message = "Support diagnostics exported. Secrets, entitlement tokens, and replayable Store proof payloads were excluded from the report."
            };
        }
        catch (Exception ex)
        {
            return CreateFailureResult($"Could not export support diagnostics: {ex.Message}");
        }
    }

    private static void WriteJsonAtomically(string path, object value)
    {
        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(value, SerializerOptions));
        File.Move(tempPath, path, overwrite: true);
    }

    private static object? CreateSanitizedProofEnvelope(StoreProofEnvelope? proofEnvelope)
    {
        if (proofEnvelope is null)
        {
            return null;
        }

        return new
        {
            proofEnvelope.SchemaVersion,
            proofEnvelope.Authority,
            proofEnvelope.Provider,
            proofEnvelope.CapturedAtUtc,
            proofEnvelope.Summary,
            Artifacts = proofEnvelope.Artifacts.Select(artifact => new
            {
                artifact.Kind,
                artifact.ContentType,
                artifact.Encoding
            })
        };
    }

    private static object CreateSanitizedStoreOwnershipConfiguration(StoreOwnershipOptions options)
    {
        return new
        {
            productKind = options.ProductKind.ToString(),
            options.IsProductKindConfigured,
            productStoreIdPresent = !string.IsNullOrWhiteSpace(options.ProductStoreId),
            inAppOfferTokenPresent = !string.IsNullOrWhiteSpace(options.InAppOfferToken),
            options.HasDurableIdentifier
        };
    }

    private static object? CreateSanitizedStoreOwnershipEvidence(StoreOwnershipEvidence? evidence)
    {
        if (evidence is null)
        {
            return null;
        }

        return new
        {
            productKind = evidence.ProductKind.ToString(),
            productStoreIdPresent = !string.IsNullOrWhiteSpace(evidence.ProductStoreId),
            inAppOfferTokenPresent = !string.IsNullOrWhiteSpace(evidence.InAppOfferToken),
            skuStoreIdPresent = !string.IsNullOrWhiteSpace(evidence.SkuStoreId),
            evidence.IsOwned,
            evidence.IsTrial,
            evidence.ExpirationDateUtc,
            evidence.VerificationSource
        };
    }

    private static SupportDiagnosticsExportResult CreateFailureResult(string message)
    {
        return new SupportDiagnosticsExportResult
        {
            IsSuccess = false,
            Message = message
        };
    }
}