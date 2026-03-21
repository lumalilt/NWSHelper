using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NWSHelper.Core.Models;

namespace NWSHelper.Gui.Services;

public sealed class ExtractionRequest
{
    public required string BoundaryCsvPath { get; init; }

    public string? ExistingAddressesCsvPath { get; init; }

    public required string DatasetRootPath { get; init; }

    public string? StatesFilterCsv { get; init; }

    public string? ConsolidatedOutputPath { get; init; }

    public int OutputSplitRows { get; init; }

    public int WarningThreshold { get; init; } = 350;

    public bool WhatIf { get; init; }

    public bool ListThresholdExceeding { get; init; }

    public bool OutputDespiteThreshold { get; init; }

    public bool OutputExistingNoneNew { get; init; }

    public bool GroupByCategory { get; init; }

    public bool NoneNewInConsolidated { get; init; }

    public bool OutputAllRows { get; init; }

    public bool ExcludeNormalizedRows { get; init; }

    public bool OverwriteExistingLatLong { get; init; }

    public bool OnlyMatchSingleState { get; init; }

    public bool OnlyMatchSingleCounty { get; init; }

    public bool PreserveRawState { get; init; }

    public bool PreserveRawStreet { get; init; }

    public bool SmartFillApartmentUnits { get; init; }

    public SmartFillApartmentUnitsMode SmartFillApartmentUnitsMode { get; init; }

    public bool PerTerritoryOutput { get; init; }

    public string? PerTerritoryDirectory { get; init; }

    public bool SmartSelect { get; init; }

    public bool SelectAll { get; init; }

    public bool NoPrompt { get; init; }

    public bool ForceWithoutAddressInput { get; init; }

    public EntitlementContext? EntitlementContext { get; init; }
}

public sealed class ExtractionProgressSnapshot
{
    public int TerritoryCount { get; init; }

    public int? TerritoryTotal { get; init; }

    public int PreExistingCount { get; init; }

    public int? PreExistingTotal { get; init; }

    public int? StreamingTotal { get; init; }

    public ProgressState? StreamState { get; init; }

    public int OutputRows { get; init; }

    public int OutputFiles { get; init; }

    public int? OutputTotalRows { get; init; }

    public int PerTerritoryRows { get; init; }

    public int PerTerritoryFiles { get; init; }

    public int? PerTerritoryTotalRows { get; init; }
}

public sealed class ExtractionPreviewData
{
    public required TerritoryExtractionPlan Plan { get; init; }

    public required ExtractionResult Result { get; init; }
}

public sealed class ExtractionExecutionData
{
    public required ExtractionResult Result { get; init; }
}

public interface IExtractionOrchestrator
{
    Task<ExtractionPreviewData> BuildPreviewAsync(ExtractionRequest request, CancellationToken cancellationToken);

    Task<ExtractionExecutionData> ExecuteAsync(
        ExtractionRequest request,
        IReadOnlyCollection<string> selectedTerritoryIds,
        Action<ExtractionProgressSnapshot>? onProgress,
        CancellationToken cancellationToken);
}
