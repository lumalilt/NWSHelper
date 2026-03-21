using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NWSHelper.Gui.Services;

public enum StoreProofAuthority
{
    None,
    Heuristic,
    Verified
}

public sealed class StoreProofArtifact
{
    public string Kind { get; init; } = string.Empty;

    public string ContentType { get; init; } = "application/json";

    public string Encoding { get; init; } = "base64";

    public string Sha256 { get; init; } = string.Empty;

    public string Payload { get; init; } = string.Empty;
}

public sealed class StoreProofSummary
{
    public string DetectionSource { get; init; } = "none";

    public bool IsPackaged { get; init; }

    public bool IsStoreInstall { get; init; }

    public string ProofLevel { get; init; } = "none";

    public string PackageFamilyName { get; init; } = string.Empty;

    public string ProcessPathHint { get; init; } = string.Empty;
}

public sealed class StoreProofEnvelope
{
    public int SchemaVersion { get; init; } = 1;

    public string Authority { get; init; } = "none";

    public string Provider { get; init; } = "microsoft-store";

    public DateTimeOffset CapturedAtUtc { get; init; }

    public StoreProofSummary Summary { get; init; } = new();

    public IReadOnlyList<StoreProofArtifact> Artifacts { get; init; } = Array.Empty<StoreProofArtifact>();
}

public sealed class StoreRuntimeContext
{
    public bool IsPackaged { get; init; }

    public bool IsStoreInstall { get; init; }

    public StoreProofAuthority ProofAuthority { get; init; }

    public string DetectionSource { get; init; } = "none";

    public string PackageFamilyName { get; init; } = string.Empty;

    public string ProcessPath { get; init; } = string.Empty;

    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public StoreOwnershipEvidence? OwnershipEvidence { get; init; }

    public StoreRuntimeContext WithVerifiedOwnership(StoreOwnershipEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        return new StoreRuntimeContext
        {
            IsPackaged = IsPackaged,
            IsStoreInstall = IsStoreInstall,
            ProofAuthority = StoreProofAuthority.Verified,
            DetectionSource = DetectionSource,
            PackageFamilyName = PackageFamilyName,
            ProcessPath = ProcessPath,
            CapturedAtUtc = CapturedAtUtc,
            OwnershipEvidence = evidence
        };
    }

    public StoreProofEnvelope? CreateProofEnvelope()
    {
        if (!IsStoreInstall || ProofAuthority == StoreProofAuthority.None)
        {
            return null;
        }

        var proofLevel = ProofAuthority.ToString().ToLowerInvariant();
        var processPathHint = BuildProcessPathHint(ProcessPath);
        var artifactPayload = new Dictionary<string, object?>
        {
            ["capturedAtUtc"] = CapturedAtUtc.ToUniversalTime().ToString("O"),
            ["detectionSource"] = DetectionSource,
            ["isPackaged"] = IsPackaged,
            ["isStoreInstall"] = IsStoreInstall,
            ["proofLevel"] = proofLevel,
            ["packageFamilyName"] = PackageFamilyName,
            ["processPathHint"] = processPathHint,
        };

        var artifactJson = JsonSerializer.Serialize(artifactPayload);
        var artifactBytes = Encoding.UTF8.GetBytes(artifactJson);

        var artifacts = new List<StoreProofArtifact>
        {
            new()
            {
                Kind = "runtime-context",
                ContentType = "application/json",
                Encoding = "base64",
                Sha256 = Convert.ToHexString(SHA256.HashData(artifactBytes)),
                Payload = Convert.ToBase64String(artifactBytes),
            }
        };

        if (OwnershipEvidence is not null)
        {
            var ownershipPayload = new Dictionary<string, object?>
            {
                ["productKind"] = OwnershipEvidence.ProductKind.ToString(),
                ["productStoreId"] = OwnershipEvidence.ProductStoreId,
                ["inAppOfferToken"] = OwnershipEvidence.InAppOfferToken,
                ["skuStoreId"] = OwnershipEvidence.SkuStoreId,
                ["isOwned"] = OwnershipEvidence.IsOwned,
                ["isTrial"] = OwnershipEvidence.IsTrial,
                ["expirationDateUtc"] = OwnershipEvidence.ExpirationDateUtc?.ToUniversalTime().ToString("O"),
                ["verificationSource"] = OwnershipEvidence.VerificationSource,
            };

            var ownershipJson = JsonSerializer.Serialize(ownershipPayload);
            var ownershipBytes = Encoding.UTF8.GetBytes(ownershipJson);
            artifacts.Add(new StoreProofArtifact
            {
                Kind = "store-ownership",
                ContentType = "application/json",
                Encoding = "base64",
                Sha256 = Convert.ToHexString(SHA256.HashData(ownershipBytes)),
                Payload = Convert.ToBase64String(ownershipBytes),
            });
        }

        return new StoreProofEnvelope
        {
            SchemaVersion = 1,
            Authority = ProofAuthority == StoreProofAuthority.Verified ? "verified" : "non-authoritative",
            Provider = "microsoft-store",
            CapturedAtUtc = CapturedAtUtc,
            Summary = new StoreProofSummary
            {
                DetectionSource = DetectionSource,
                IsPackaged = IsPackaged,
                IsStoreInstall = IsStoreInstall,
                ProofLevel = proofLevel,
                PackageFamilyName = PackageFamilyName,
                ProcessPathHint = processPathHint,
            },
            Artifacts = artifacts
        };
    }

    private static string BuildProcessPathHint(string processPath)
    {
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return string.Empty;
        }

        if (processPath.IndexOf("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "windowsapps-path";
        }

        return Path.GetFileName(processPath);
    }
}

public interface IStoreRuntimeContextProvider
{
    StoreRuntimeContext GetCurrent();
}

public sealed class StoreRuntimeContextProvider : IStoreRuntimeContextProvider
{
    private readonly Func<string, string?> environmentReader;
    private readonly Func<string?> processPathReader;

    public StoreRuntimeContextProvider(
        Func<string, string?>? environmentReader = null,
        Func<string?>? processPathReader = null)
    {
        this.environmentReader = environmentReader ?? Environment.GetEnvironmentVariable;
        this.processPathReader = processPathReader ?? (() => Environment.ProcessPath);
    }

    public StoreRuntimeContext GetCurrent()
    {
        var now = DateTimeOffset.UtcNow;
        var processPath = ReadTrimmedValue(() => processPathReader());
        var packageFamilyName = ReadTrimmedValue(() => environmentReader("NWSHELPER_STORE_PACKAGE_FAMILY_NAME"));
        var forceStoreChannel = ReadTrimmedValue(() => environmentReader("NWSHELPER_FORCE_STORE_CHANNEL"));

        if (string.Equals(forceStoreChannel, "1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(forceStoreChannel, "true", StringComparison.OrdinalIgnoreCase))
        {
            return new StoreRuntimeContext
            {
                IsPackaged = true,
                IsStoreInstall = true,
                ProofAuthority = StoreProofAuthority.Heuristic,
                DetectionSource = "environment-override",
                PackageFamilyName = packageFamilyName,
                ProcessPath = processPath,
                CapturedAtUtc = now,
            };
        }

        if (processPath.IndexOf("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return new StoreRuntimeContext
            {
                IsPackaged = true,
                IsStoreInstall = true,
                ProofAuthority = StoreProofAuthority.Heuristic,
                DetectionSource = "windowsapps-path",
                PackageFamilyName = packageFamilyName,
                ProcessPath = processPath,
                CapturedAtUtc = now,
            };
        }

        return new StoreRuntimeContext
        {
            IsPackaged = false,
            IsStoreInstall = false,
            ProofAuthority = StoreProofAuthority.None,
            DetectionSource = "none",
            PackageFamilyName = packageFamilyName,
            ProcessPath = processPath,
            CapturedAtUtc = now,
        };
    }

    private static string ReadTrimmedValue(Func<string?> valueFactory)
    {
        return valueFactory()?.Trim() ?? string.Empty;
    }
}