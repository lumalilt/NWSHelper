using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NWSHelper.Core;
using NWSHelper.Core.Models;
using NWSHelper.Core.Providers;
using NWSHelper.Core.Services;
using NWSHelper.Core.Utils;

namespace NWSHelper.Gui.Services;

public sealed class ExtractionOrchestrator : IExtractionOrchestrator
{
    private readonly IBoundaryReader boundaryReader;
    private readonly IAddressSource addressSource;

    public ExtractionOrchestrator()
        : this(new BoundaryReader(), new OpenAddressesProvider())
    {
    }

    internal ExtractionOrchestrator(IBoundaryReader boundaryReader, IAddressSource addressSource)
    {
        this.boundaryReader = boundaryReader;
        this.addressSource = addressSource;
    }

    public async Task<ExtractionPreviewData> BuildPreviewAsync(ExtractionRequest request, CancellationToken cancellationToken)
    {
        var run = await BuildRunContextAsync(request, cancellationToken);
        var extractor = new AddressExtractor(boundaryReader, addressSource);
        TerritoryExtractionPlan? plan = null;

        var result = await extractor.ExtractAsync(
            run.BoundaryCsvPath,
            run.ExistingAddressesCsvPath,
            run.DatasetRootPath,
            run.States,
            run.ConsolidatedOutputPath,
            outputSplit: run.OutputSplitRows,
            perTerritoryOutputDirectory: run.PerTerritoryOutputDirectory,
            warningThreshold: run.WarningThreshold,
            whatIf: true,
            outputExistingAddressesDespiteThreshold: run.OutputDespiteThreshold,
            outputExistingNoneNew: run.OutputExistingNoneNew,
            groupByCategory: run.GroupByCategory,
            noneNewConsolidated: run.NoneNewInConsolidated,
            outputAllRows: run.OutputAllRows,
            excludeNormalizedRows: run.ExcludeNormalizedRows,
            overwriteExistingLatLong: run.OverwriteExistingLatLong,
            allowNumberStreetSuburbFallback: run.AllowNumberStreetSuburbFallback,
            normalizeState: !run.PreserveRawState,
            normalizeStreet: !run.PreserveRawStreet,
            smartFillApartmentUnits: run.SmartFillApartmentUnits,
            smartFillApartmentUnitsMode: run.SmartFillApartmentUnitsMode,
            entitlementContext: request.EntitlementContext,
            selectionProvider: (incomingPlan, _) =>
            {
                plan = incomingPlan;
                return Task.FromResult((IReadOnlyCollection<string>)ResolveDefaultSelection(incomingPlan, run));
            },
            cancellationToken: cancellationToken);

        return new ExtractionPreviewData
        {
            Plan = plan ?? new TerritoryExtractionPlan(),
            Result = result
        };
    }

    public async Task<ExtractionExecutionData> ExecuteAsync(
        ExtractionRequest request,
        IReadOnlyCollection<string> selectedTerritoryIds,
        Action<ExtractionProgressSnapshot>? onProgress,
        CancellationToken cancellationToken)
    {
        var run = await BuildRunContextAsync(request, cancellationToken);
        var extractor = new AddressExtractor(boundaryReader, addressSource);

        var snapshot = new ProgressAccumulator
        {
            TerritoryTotal = run.TerritoryTotal,
            PreExistingTotal = run.PreExistingTotal,
            StreamingTotal = run.StreamingTotal
        };

        onProgress?.Invoke(snapshot.ToSnapshot());

        var result = await extractor.ExtractAsync(
            run.BoundaryCsvPath,
            run.ExistingAddressesCsvPath,
            run.DatasetRootPath,
            run.States,
            run.ConsolidatedOutputPath,
            outputSplit: run.OutputSplitRows,
            perTerritoryOutputDirectory: run.PerTerritoryOutputDirectory,
            warningThreshold: run.WarningThreshold,
            whatIf: run.EffectiveWhatIf,
            outputExistingAddressesDespiteThreshold: run.OutputDespiteThreshold,
            outputExistingNoneNew: run.OutputExistingNoneNew,
            groupByCategory: run.GroupByCategory,
            noneNewConsolidated: run.NoneNewInConsolidated,
            outputAllRows: run.OutputAllRows,
            excludeNormalizedRows: run.ExcludeNormalizedRows,
            overwriteExistingLatLong: run.OverwriteExistingLatLong,
            allowNumberStreetSuburbFallback: run.AllowNumberStreetSuburbFallback,
            normalizeState: !run.PreserveRawState,
            normalizeStreet: !run.PreserveRawStreet,
            smartFillApartmentUnits: run.SmartFillApartmentUnits,
            smartFillApartmentUnitsMode: run.SmartFillApartmentUnitsMode,
            entitlementContext: request.EntitlementContext,
            selectionProvider: (plan, _) => Task.FromResult((IReadOnlyCollection<string>)ResolveExplicitSelection(plan, selectedTerritoryIds)),
            territoryProgress: count =>
            {
                snapshot.TerritoryCount = count;
                onProgress?.Invoke(snapshot.ToSnapshot());
            },
            preExistingProgress: count =>
            {
                snapshot.PreExistingCount = count;
                onProgress?.Invoke(snapshot.ToSnapshot());
            },
            outputProgress: (rows, files) =>
            {
                snapshot.OutputRows = rows;
                snapshot.OutputFiles = files;
                onProgress?.Invoke(snapshot.ToSnapshot());
            },
            outputTotals: (rows, files) =>
            {
                snapshot.OutputTotalRows = rows;
                snapshot.OutputFiles = files;
                onProgress?.Invoke(snapshot.ToSnapshot());
            },
            perTerritoryProgress: (rows, files) =>
            {
                snapshot.PerTerritoryRows = rows;
                snapshot.PerTerritoryFiles = files;
                onProgress?.Invoke(snapshot.ToSnapshot());
            },
            perTerritoryTotals: (rows, files) =>
            {
                snapshot.PerTerritoryTotalRows = rows;
                snapshot.PerTerritoryFiles = files;
                onProgress?.Invoke(snapshot.ToSnapshot());
            },
            progress: state =>
            {
                snapshot.StreamState = state;
                onProgress?.Invoke(snapshot.ToSnapshot());
            },
            cancellationToken: cancellationToken);

        return new ExtractionExecutionData
        {
            Result = result
        };
    }

    private async Task<ResolvedRunContext> BuildRunContextAsync(ExtractionRequest request, CancellationToken cancellationToken)
    {
        var boundaryPath = Path.GetFullPath(request.BoundaryCsvPath);
        var existingPath = string.IsNullOrWhiteSpace(request.ExistingAddressesCsvPath)
            ? null
            : Path.GetFullPath(request.ExistingAddressesCsvPath);
        var datasetRootPath = Path.GetFullPath(request.DatasetRootPath);
        var outputPath = ResolveOutputPath(request.ConsolidatedOutputPath, boundaryPath);
        var perTerritoryOutputDirectory = ResolvePerTerritoryOutputDirectory(request, outputPath);
        var states = ParseStates(request.StatesFilterCsv, datasetRootPath);
        var allowFallback = AllowNumberStreetSuburbFallback(request.StatesFilterCsv, request.OnlyMatchSingleState, request.OnlyMatchSingleCounty);
        var effectiveWhatIf = request.WhatIf || request.ListThresholdExceeding;

        int? streamingTotal = null;
        if (addressSource is OpenAddressesProvider openAddressesProvider)
        {
            streamingTotal = await openAddressesProvider.CountAddressesAsync(datasetRootPath, states, cancellationToken);
        }

        int? preExistingTotal = null;
        if (!string.IsNullOrWhiteSpace(existingPath) && File.Exists(existingPath))
        {
            preExistingTotal = await CountCsvRowsAsync(existingPath, cancellationToken);
        }

        int? territoryTotal = null;
        if (File.Exists(boundaryPath))
        {
            territoryTotal = await CountTerritoryRowsAsync(boundaryPath, cancellationToken);
        }

        return new ResolvedRunContext(
            boundaryPath,
            existingPath,
            datasetRootPath,
            outputPath,
            request.OutputSplitRows,
            request.WarningThreshold <= 0 ? 350 : request.WarningThreshold,
            request.OutputDespiteThreshold,
            request.OutputExistingNoneNew,
            request.GroupByCategory,
            request.NoneNewInConsolidated,
            request.OutputAllRows,
            request.ExcludeNormalizedRows,
            request.OverwriteExistingLatLong,
            request.PreserveRawState,
            request.PreserveRawStreet,
            request.SmartFillApartmentUnits,
            request.SmartFillApartmentUnitsMode,
            allowFallback,
            request.SmartSelect,
            request.SelectAll,
            request.NoPrompt,
            effectiveWhatIf,
            perTerritoryOutputDirectory,
            states,
            territoryTotal,
            preExistingTotal,
            streamingTotal);
    }

    private static IReadOnlyCollection<string> ResolveDefaultSelection(TerritoryExtractionPlan plan, ResolvedRunContext run)
    {
        var effectiveSmartSelect = run.SmartSelect || run.NoPrompt;
        return plan.Items
            .Where(item => run.SelectAll || (effectiveSmartSelect && !item.HasWarning))
            .Select(item => item.TerritoryId)
            .ToList();
    }

    private static IReadOnlyCollection<string> ResolveExplicitSelection(
        TerritoryExtractionPlan plan,
        IReadOnlyCollection<string> selectedTerritoryIds)
    {
        var selected = new HashSet<string>(selectedTerritoryIds, StringComparer.OrdinalIgnoreCase);
        return plan.Items
            .Where(item => selected.Contains(item.TerritoryId))
            .Select(item => item.TerritoryId)
            .ToList();
    }

    private static string ResolveOutputPath(string? requestedOutputPath, string boundaryPath)
    {
        if (!string.IsNullOrWhiteSpace(requestedOutputPath))
        {
            var normalizedRequestPath = Path.GetFullPath(requestedOutputPath);
            if (string.Equals(Path.GetExtension(normalizedRequestPath), ".csv", StringComparison.OrdinalIgnoreCase))
            {
                return normalizedRequestPath;
            }

            return BuildDefaultOutputFilePath(boundaryPath, normalizedRequestPath);
        }

        var boundaryDirectory = Path.GetDirectoryName(boundaryPath) ?? Environment.CurrentDirectory;
        var defaultOutputDirectory = Path.Combine(boundaryDirectory, "NWSHelper-Output");
        return BuildDefaultOutputFilePath(boundaryPath, defaultOutputDirectory);
    }

    private static string? ResolvePerTerritoryOutputDirectory(ExtractionRequest request, string outputPath)
    {
        return request.PerTerritoryOutput
            ? Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? Environment.CurrentDirectory
            : null;
    }

    private static string BuildDefaultOutputFilePath(string boundaryPath, string outputDirectory)
    {
        var inputFileName = Path.GetFileNameWithoutExtension(boundaryPath);
        if (string.IsNullOrWhiteSpace(inputFileName))
        {
            inputFileName = "Territories";
        }

        var resolvedOutputDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(resolvedOutputDirectory);

        var timestamp = DateTime.Now.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture);
        var fileName = $"NWSH-Updated-{inputFileName}.{timestamp}.csv";
        return Path.Combine(resolvedOutputDirectory, fileName);
    }

    private static IReadOnlyList<string> ParseStates(string? statesCsv, string datasetRoot)
    {
        if (!string.IsNullOrWhiteSpace(statesCsv))
        {
            return statesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => s.ToUpperInvariant())
                .ToList();
        }

        var path = Path.Combine(datasetRoot, "openaddresses", "us");
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"Dataset root not found: {path}");
        }

        var states = Directory.GetDirectories(path)
            .Select(Path.GetFileName)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!.ToUpperInvariant())
            .ToList();

        if (states.Count == 0)
        {
            throw new DirectoryNotFoundException($"No OpenAddresses state folders found under {path}.");
        }

        return states;
    }

    private static bool AllowNumberStreetSuburbFallback(string? statesCsv, bool onlyMatchOnSingleState, bool onlyMatchOnSingleCounty)
    {
        if (!onlyMatchOnSingleState && !onlyMatchOnSingleCounty)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(statesCsv))
        {
            return false;
        }

        var stateSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var countySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allHaveCounty = true;

        foreach (var raw in statesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var parts = raw.Split(':', 2, StringSplitOptions.TrimEntries);
            var state = parts[0].Trim();
            if (!string.IsNullOrWhiteSpace(state))
            {
                stateSet.Add(state);
            }

            if (parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]))
            {
                countySet.Add(parts[1]);
            }
            else
            {
                allHaveCounty = false;
            }
        }

        if (onlyMatchOnSingleState && stateSet.Count != 1)
        {
            return false;
        }

        if (onlyMatchOnSingleCounty && (!allHaveCounty || countySet.Count != 1))
        {
            return false;
        }

        return true;
    }

    private static async Task<int> CountCsvRowsAsync(string path, CancellationToken cancellationToken)
    {
        var count = 0;
        await using var stream = File.OpenRead(path);
        using var reader = new StreamReader(stream);

        var isHeader = true;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (isHeader)
            {
                isHeader = false;
                continue;
            }

            if (!string.IsNullOrWhiteSpace(line))
            {
                count++;
            }
        }

        return count;
    }

    private static async Task<int> CountTerritoryRowsAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        using var reader = new StreamReader(stream);
        using var csv = CsvFactory.CreateReader(reader);

        await csv.ReadAsync();
        csv.ReadHeader();
        var header = csv.HeaderRecord ?? Array.Empty<string>();
        var headerSet = new HashSet<string>(header.Select(h => h.Trim()), StringComparer.OrdinalIgnoreCase);

        if (!headerSet.Contains("TerritoryID") || !headerSet.Contains("Boundary"))
        {
            return 1;
        }

        var count = 0;
        while (await csv.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            count++;
        }

        return count;
    }

    private sealed record ResolvedRunContext(
        string BoundaryCsvPath,
        string? ExistingAddressesCsvPath,
        string DatasetRootPath,
        string ConsolidatedOutputPath,
        int OutputSplitRows,
        int WarningThreshold,
        bool OutputDespiteThreshold,
        bool OutputExistingNoneNew,
        bool GroupByCategory,
        bool NoneNewInConsolidated,
        bool OutputAllRows,
        bool ExcludeNormalizedRows,
        bool OverwriteExistingLatLong,
        bool PreserveRawState,
        bool PreserveRawStreet,
        bool SmartFillApartmentUnits,
        SmartFillApartmentUnitsMode SmartFillApartmentUnitsMode,
        bool AllowNumberStreetSuburbFallback,
        bool SmartSelect,
        bool SelectAll,
        bool NoPrompt,
        bool EffectiveWhatIf,
        string? PerTerritoryOutputDirectory,
        IReadOnlyList<string> States,
        int? TerritoryTotal,
        int? PreExistingTotal,
        int? StreamingTotal);

    private sealed class ProgressAccumulator
    {
        public int TerritoryCount { get; set; }

        public int? TerritoryTotal { get; init; }

        public int PreExistingCount { get; set; }

        public int? PreExistingTotal { get; init; }

        public int? StreamingTotal { get; init; }

        public ProgressState? StreamState { get; set; }

        public int OutputRows { get; set; }

        public int OutputFiles { get; set; }

        public int? OutputTotalRows { get; set; }

        public int PerTerritoryRows { get; set; }

        public int PerTerritoryFiles { get; set; }

        public int? PerTerritoryTotalRows { get; set; }

        public ExtractionProgressSnapshot ToSnapshot()
        {
            return new ExtractionProgressSnapshot
            {
                TerritoryCount = TerritoryCount,
                TerritoryTotal = TerritoryTotal,
                PreExistingCount = PreExistingCount,
                PreExistingTotal = PreExistingTotal,
                StreamingTotal = StreamingTotal,
                StreamState = StreamState,
                OutputRows = OutputRows,
                OutputFiles = OutputFiles,
                OutputTotalRows = OutputTotalRows,
                PerTerritoryRows = PerTerritoryRows,
                PerTerritoryFiles = PerTerritoryFiles,
                PerTerritoryTotalRows = PerTerritoryTotalRows
            };
        }
    }
}
