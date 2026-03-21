using System.CommandLine;
using System.CommandLine.Invocation;
using System.Globalization;
using NWSHelper.Core;
using NWSHelper.Core.Exceptions;
using NWSHelper.Core.Models;
using NWSHelper.Core.Providers;
using NWSHelper.Core.Services;
using NWSHelper.Core.Utils;
using NWSHelper.Cli.Services;

namespace NWSHelper.Cli.Commands;

/// <summary>
/// Defines the extract command.
/// </summary>
public static class ExtractCommand
{
    /// <summary>
    /// Creates the extract command with all options and handler wiring.
    /// </summary>
    public static Command Create()
    {
        var boundary = new Option<string>("--boundary-csv", description: "Path to the territory boundary CSV (supports a Boundary column with [lon,lat] pairs or Latitude/Longitude columns)") { IsRequired = true };
        var addresses = new Option<string?>("--addresses-csv", description: "Optional pre-existing territory addresses CSV to merge with extracted matches");
        var states = new Option<string?>("--states", description: "Comma-separated state filters, e.g. MD,VA or MD:baltimore_county; auto-discovers all states when omitted");
        var datasetRoot = new Option<string>("--dataset-root", () => "./datasets", "Root folder containing openaddresses/us/<state> data");
        var output = new Option<string?>("--out", description: "Consolidated output CSV path (defaults to <boundary>_extracted.csv)");
        var outputSplit = new Option<int>("--output-split", () => 0, "Max address records per output file; 0 disables splitting");
        var warningThreshold = new Option<int>("--warning-threshold", () => 350, "Per-territory total count at/above which a territory is flagged with a warning");
        var perTerritory = new Option<bool>("--per-territory-output", description: "Emit per-territory CSV outputs; defaults to the --out directory");
        perTerritory.AddAlias("--per-territory");
        var forceWithoutAddressInput = new Option<bool>("--force-without-address-input", description: "Skip confirmation when territories lack pre-existing address rows");
        var smartSelect = new Option<bool>("--smart-select", description: "Pre-select non-warning territories in the interactive selection menu");
        var selectAll = new Option<bool>("--select-all", description: "Select all territories in the interactive selection menu (including warning territories)");
        var noPrompt = new Option<bool>("--no-prompt", description: "Non-interactive mode: skip prompts/selection and choose non-warning territories unless --select-all is provided");
        var whatIf = new Option<bool>("--what-if", description: "Evaluate extraction and threshold outcomes without writing output files");
        var outputDespiteThreshold = new Option<bool>("--output-existing-addresses-despite-threshold", description: "Write per-territory outputs even for warning territories (at/above --warning-threshold)");
        var outputExistingNoneNew = new Option<bool>("--output-existing-none-new", description: "Emit per-territory outputs when a territory has no new addresses but has pre-existing rows");
        var groupByCategory = new Option<bool>("--group-by-category", description: "Write additional Category-<Category>.csv outputs grouped by territory category");
        var noneNewConsolidated = new Option<bool>("--nonenew-consolidated", description: "Keep territories with no new addresses (and no geofill/addfill changes) in the consolidated output; still writes None-New-Territory-Addresses.csv when applicable");
        var outputAllRows = new Option<bool>("--output-all-rows", description: "Write all merged rows (legacy behavior). Default writes only delta rows (new + updated existing rows).");
        var excludeNormalizedRows = new Option<bool>("--exclude-normalized-rows", description: "In delta mode, exclude rows changed only by normalization (legacy delta behavior).");
        var overwriteExistingLatLong = new Option<bool>("--overwrite-existing-latlong", description: "On exact matches, overwrite existing latitude/longitude with dataset values (default only backfills missing coordinates)");
        var onlyMatchOnSingleState = new Option<bool>("--only-match-on-single-state", description: "Allow Number+Street+Suburb fallback matching only when a single state is provided in --states");
        var onlyMatchOnSingleCounty = new Option<bool>("--only-match-on-single-county", description: "Allow Number+Street+Suburb fallback matching only when a single county is provided in --states");
        var nonormalizeState = new Option<bool>("--nonormalize-state", description: "Preserve raw State values in output instead of normalizing to postal abbreviations");
        var nonormalizeStreet = new Option<bool>("--nonormalize-street", description: "Preserve raw Street values in output instead of normalizing street suffixes");
        var smartFillApartmentUnits = new Option<string?>("--smart-fill-apartment-units", description: "Synthesize missing ApartmentNumber rows per territory. Optional mode: TypeApartmentOnly (default), AllTypes, or ApartmentCategoryOnly")
        {
            Arity = ArgumentArity.ZeroOrOne
        };
        var listThresholdExceeding = new Option<bool>("--list-threshold-exceeding", description: "List territories exceeding the warning threshold and exit without writing output files");

        var command = new Command("extract", "Extract and merge boundary-matched addresses (shows progress indicators)")
        {
            boundary, addresses, states, datasetRoot, output, outputSplit, warningThreshold, perTerritory, forceWithoutAddressInput, smartSelect, selectAll, noPrompt, whatIf, outputDespiteThreshold, outputExistingNoneNew, groupByCategory, noneNewConsolidated, outputAllRows, excludeNormalizedRows, overwriteExistingLatLong, onlyMatchOnSingleState, onlyMatchOnSingleCounty, nonormalizeState, nonormalizeStreet, smartFillApartmentUnits, listThresholdExceeding
        };

        command.SetHandler(async (InvocationContext context) =>
        {
            var boundaryCsv = context.ParseResult.GetValueForOption(boundary)!;
            var addressesCsv = context.ParseResult.GetValueForOption(addresses);
            var statesCsv = context.ParseResult.GetValueForOption(states);
            var datasetRootValue = context.ParseResult.GetValueForOption(datasetRoot)!;
            var outPath = context.ParseResult.GetValueForOption(output);
            var outputSplitValue = context.ParseResult.GetValueForOption(outputSplit);
            var warningThresholdValue = context.ParseResult.GetValueForOption(warningThreshold);
            var perTerritoryValue = context.ParseResult.GetValueForOption(perTerritory);
            var forceWithoutAddressInputValue = context.ParseResult.GetValueForOption(forceWithoutAddressInput);
            var smartSelectValue = context.ParseResult.GetValueForOption(smartSelect);
            var selectAllValue = context.ParseResult.GetValueForOption(selectAll);
            var noPromptValue = context.ParseResult.GetValueForOption(noPrompt);
            var whatIfValue = context.ParseResult.GetValueForOption(whatIf);
            var outputDespiteThresholdValue = context.ParseResult.GetValueForOption(outputDespiteThreshold);
            var outputExistingNoneNewValue = context.ParseResult.GetValueForOption(outputExistingNoneNew);
            var groupByCategoryValue = context.ParseResult.GetValueForOption(groupByCategory);
            var noneNewConsolidatedValue = context.ParseResult.GetValueForOption(noneNewConsolidated);
            var outputAllRowsValue = context.ParseResult.GetValueForOption(outputAllRows);
            var excludeNormalizedRowsValue = context.ParseResult.GetValueForOption(excludeNormalizedRows);
            var overwriteExistingLatLongValue = context.ParseResult.GetValueForOption(overwriteExistingLatLong);
            var onlyMatchOnSingleStateValue = context.ParseResult.GetValueForOption(onlyMatchOnSingleState);
            var onlyMatchOnSingleCountyValue = context.ParseResult.GetValueForOption(onlyMatchOnSingleCounty);
            var nonormalizeStateValue = context.ParseResult.GetValueForOption(nonormalizeState);
            var nonormalizeStreetValue = context.ParseResult.GetValueForOption(nonormalizeStreet);
            var smartFillApartmentUnitsValue = context.ParseResult.GetValueForOption(smartFillApartmentUnits);
            var listThresholdExceedingValue = context.ParseResult.GetValueForOption(listThresholdExceeding);

            var smartFillApartmentUnitsSpecified = context.ParseResult.FindResultFor(smartFillApartmentUnits) is not null;
            var smartFillApartmentUnitsEnabled = smartFillApartmentUnitsSpecified;
            var smartFillApartmentUnitsMode = SmartFillApartmentUnitsMode.TypeApartmentOnly;
            if (smartFillApartmentUnitsEnabled)
            {
                if (!TryParseSmartFillApartmentUnitsMode(smartFillApartmentUnitsValue, out smartFillApartmentUnitsMode))
                {
                    Console.Error.WriteLine("Error: --smart-fill-apartment-units mode must be one of: TypeApartmentOnly, AllTypes, ApartmentCategoryOnly.");
                    context.ExitCode = 1;
                    return;
                }
            }

            var boundaryFile = new FileInfo(boundaryCsv);
            var addressesFile = string.IsNullOrWhiteSpace(addressesCsv) ? null : new FileInfo(addressesCsv);
            var datasetRootDir = new DirectoryInfo(datasetRootValue);

            var resolvedOutput = string.IsNullOrWhiteSpace(outPath)
                ? Path.Combine(Environment.CurrentDirectory, Path.GetFileNameWithoutExtension(boundaryFile.Name) + "_extracted.csv")
                : Path.GetFullPath(outPath);

            var perTerritoryOutputDirectory = perTerritoryValue
                ? Path.GetDirectoryName(resolvedOutput) ?? Environment.CurrentDirectory
                : null;

            var boundaryReader = new BoundaryReader();
            var provider = new OpenAddressesProvider();
            var extractor = new AddressExtractor(boundaryReader, provider);

            try
            {
                var stateList = ParseStates(statesCsv, datasetRootDir.FullName);
                var allowNumberStreetSuburbFallback = AllowNumberStreetSuburbFallback(statesCsv, onlyMatchOnSingleStateValue, onlyMatchOnSingleCountyValue);
                var entitlementContext = BuildCliEntitlementContext();

                if (outputSplitValue < 0)
                {
                    Console.Error.WriteLine("Error: --output-split must be 0 or greater.");
                    context.ExitCode = 1;
                    return;
                }

                if (!boundaryFile.Exists)
                {
                    Console.Error.WriteLine($"Error: Boundary CSV not found: {boundaryFile.FullName}");
                    context.ExitCode = 2;
                    return;
                }

                var territoryCount = 0;
                var preExistingCount = 0;
                var outputRows = 0;
                var outputFiles = 0;
                var perTerritoryRows = 0;
                var perTerritoryFiles = 0;
                ProgressState? lastProgressState = null;
                var progressEnabled = !Console.IsOutputRedirected && !Console.IsErrorRedirected;
                var progressRenderer = progressEnabled ? new SpectreProgressRenderer() : null;
                int? streamingTotal = null;
                int? preExistingTotal = null;
                int? territoryTotal = null;
                int? outputTotalRows = null;
                int? perTerritoryTotalRows = null;

                Task<ExtractionResult> RunExtractionAsync(
                    Action<int>? territoryProgress,
                    Action<int>? preExistingProgress,
                    Action<int, int>? outputProgress,
                    Action<int, int>? outputTotals,
                    Action<int, int>? perTerritoryProgress,
                    Action<int, int>? perTerritoryTotals,
                    Action<ProgressState>? progress)
                {
                    return extractor.ExtractAsync(
                        boundaryFile.FullName,
                        addressesFile?.FullName,
                        datasetRootDir.FullName,
                        stateList,
                        resolvedOutput,
                        outputSplit: outputSplitValue,
                        perTerritoryOutputDirectory: perTerritoryOutputDirectory,
                        warningThresholdValue,
                        listThresholdExceedingValue || whatIfValue,
                        outputDespiteThresholdValue,
                        outputExistingNoneNewValue,
                        groupByCategory: groupByCategoryValue,
                        noneNewConsolidated: noneNewConsolidatedValue,
                        outputAllRows: outputAllRowsValue,
                        excludeNormalizedRows: excludeNormalizedRowsValue,
                        overwriteExistingLatLong: overwriteExistingLatLongValue,
                        allowNumberStreetSuburbFallback: allowNumberStreetSuburbFallback,
                        normalizeState: !nonormalizeStateValue,
                        normalizeStreet: !nonormalizeStreetValue,
                        smartFillApartmentUnits: smartFillApartmentUnitsEnabled,
                        smartFillApartmentUnitsMode: smartFillApartmentUnitsMode,
                        entitlementContext: entitlementContext,
                        selectionProvider: (plan, ct) => HandlePreWriteSelectionAsync(plan, addressesFile is not null, forceWithoutAddressInputValue, smartSelectValue, selectAllValue, noPromptValue, progressRenderer, ct),
                        territoryProgress: territoryProgress,
                        preExistingProgress: preExistingProgress,
                        outputProgress: outputProgress,
                        perTerritoryProgress: perTerritoryProgress,
                        outputTotals: outputTotals,
                        perTerritoryTotals: perTerritoryTotals,
                        progress: progress,
                        cancellationToken: context.GetCancellationToken());
                }

                ExtractionResult result;
                if (progressRenderer is not null)
                {
                    ExtractionResult? innerResult = null;
                    if (provider is OpenAddressesProvider openAddressesProvider)
                    {
                        streamingTotal = await openAddressesProvider.CountAddressesAsync(datasetRootDir.FullName, stateList, context.GetCancellationToken());
                    }

                    if (addressesFile is not null)
                    {
                        preExistingTotal = await CountCsvRowsAsync(addressesFile.FullName, context.GetCancellationToken());
                    }

                    await progressRenderer.RunAsync(async () =>
                    {
                        var outputTasksStarted = false;
                        var perTerritoryStarted = false;

                        try
                        {
                            progressRenderer.StartTask("territories", "Loading territories...", isIndeterminate: true);
                            if (addressesFile is not null)
                            {
                                progressRenderer.StartTask("preexisting", "Loading pre-existing addresses...", maxValue: preExistingTotal, isIndeterminate: preExistingTotal is null || preExistingTotal == 0);
                            }
                            progressRenderer.StartTask("streaming", "Streaming & matching addresses...", maxValue: streamingTotal, isIndeterminate: streamingTotal is null || streamingTotal == 0);
                            if (!(listThresholdExceedingValue || whatIfValue))
                            {
                                progressRenderer.StartTask("output", "Writing output files...", isIndeterminate: true);
                                outputTasksStarted = true;
                                if (!string.IsNullOrWhiteSpace(perTerritoryOutputDirectory))
                                {
                                    progressRenderer.StartTask("perterritory", "Writing per-territory files...", isIndeterminate: true);
                                    perTerritoryStarted = true;
                                }
                            }

                            innerResult = await RunExtractionAsync(count =>
                            {
                                territoryCount = count;
                                progressRenderer.UpdateTask("territories", value: territoryCount, maxValue: territoryTotal, description: $"Loading territories... ({territoryCount})");
                            }, count =>
                            {
                                preExistingCount = count;
                                progressRenderer.UpdateTask("preexisting", value: preExistingCount, maxValue: preExistingTotal, description: $"Loading pre-existing addresses... ({preExistingCount:n0})");
                            }, (rows, files) =>
                            {
                                outputRows = rows;
                                outputFiles = files;
                                progressRenderer.UpdateTask("output", value: outputRows, maxValue: outputTotalRows, description: $"Writing output files... ({outputRows:n0} rows, {outputFiles:n0} files)");
                            }, (rows, files) =>
                            {
                                outputTotalRows = rows;
                                outputFiles = files;
                                progressRenderer.UpdateTask("output", maxValue: outputTotalRows, description: $"Writing output files... ({outputRows:n0} rows, {outputFiles:n0} files)");
                            }, (rows, files) =>
                            {
                                perTerritoryRows = rows;
                                perTerritoryFiles = files;
                                progressRenderer.UpdateTask("perterritory", value: perTerritoryRows, maxValue: perTerritoryTotalRows, description: $"Writing per-territory files... ({perTerritoryRows:n0} rows, {perTerritoryFiles:n0} files)");
                            }, (rows, files) =>
                            {
                                perTerritoryTotalRows = rows;
                                perTerritoryFiles = files;
                                progressRenderer.UpdateTask("perterritory", maxValue: perTerritoryTotalRows, description: $"Writing per-territory files... ({perTerritoryRows:n0} rows, {perTerritoryFiles:n0} files)");
                            }, state =>
                            {
                                lastProgressState = state;
                                var summary = $"Streaming & matching addresses... ({state.ProcessedRows:n0} processed, {state.MatchedRows:n0} matched, {state.NewRows:n0} new)";
                                progressRenderer.UpdateTask("streaming", value: state.ProcessedRows, maxValue: streamingTotal, description: summary);
                            });
                            progressRenderer.CompleteTask("territories", $"Loaded {territoryCount} territories");
                            if (addressesFile is not null)
                            {
                                progressRenderer.CompleteTask("preexisting", $"Loaded {preExistingCount:n0} pre-existing addresses");
                            }
                            if (lastProgressState is not null)
                            {
                                var summary = $"Processed {lastProgressState.ProcessedRows:n0} addresses";
                                progressRenderer.CompleteTask("streaming", summary);
                            }
                            else
                            {
                                progressRenderer.CompleteTask("streaming", "Streaming complete");
                            }
                            if (outputTasksStarted)
                            {
                                progressRenderer.CompleteTask("output", $"Wrote {outputRows:n0} rows across {outputFiles:n0} files");
                            }
                            if (perTerritoryStarted)
                            {
                                progressRenderer.CompleteTask("perterritory", $"Wrote {perTerritoryRows:n0} rows across {perTerritoryFiles:n0} files");
                            }
                        }
                        catch
                        {
                            progressRenderer.SetError("territories", "Territory loading failed");
                            if (addressesFile is not null)
                            {
                                progressRenderer.SetError("preexisting", "Pre-existing load failed");
                            }
                            progressRenderer.SetError("streaming", "Streaming failed");
                            if (outputTasksStarted)
                            {
                                progressRenderer.SetError("output", "Output writing failed");
                            }
                            if (perTerritoryStarted)
                            {
                                progressRenderer.SetError("perterritory", "Per-territory writing failed");
                            }
                            throw;
                        }
                    }, context.GetCancellationToken());
                    result = innerResult ?? throw new InvalidOperationException("Extraction did not return a result.");
                }
                else
                {
                    result = await RunExtractionAsync(
                        count => territoryCount = count,
                        count => preExistingCount = count,
                        (rows, files) => { outputRows = rows; outputFiles = files; },
                        (rows, files) => { outputTotalRows = rows; outputFiles = files; },
                        (rows, files) => { perTerritoryRows = rows; perTerritoryFiles = files; },
                        (rows, files) => { perTerritoryTotalRows = rows; perTerritoryFiles = files; },
                        state => lastProgressState = state);
                }

                var effectiveThreshold = warningThresholdValue <= 0 ? 350 : warningThresholdValue;

                if (listThresholdExceedingValue)
                {
                    var thresholdLines = BuildThresholdExceedingLines(result, effectiveThreshold, outputDespiteThresholdValue);
                    if (thresholdLines.Count == 0)
                    {
                        Console.WriteLine($"No territories exceed the warning threshold (> {effectiveThreshold}).");
                    }
                    else
                    {
                        Console.WriteLine($"Territories exceeding the warning threshold (> {effectiveThreshold}):");
                        foreach (var line in thresholdLines)
                        {
                            Console.WriteLine($"  {line}");
                        }
                    }

                    context.ExitCode = 0;
                    return;
                }

                var territoryIds = result.TerritoryDisplayNames.Keys
                    .Concat(result.TerritoryAddressCounts.Keys)
                    .Concat(result.TerritoryOutputFiles.Keys)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(id => BuildTerritorySortKey(id, result))
                    .ToList();

                if (territoryIds.Count > 0)
                {
                    Console.WriteLine("Territories:");
                    foreach (var id in territoryIds)
                    {
                        var label = result.TerritoryDisplayNames.TryGetValue(id, out var display) ? display : id;
                        var total = result.TerritoryAddressCounts.TryGetValue(id, out var totalCount)
                            ? totalCount
                            : result.TerritoryAddressCountsEvaluated.TryGetValue(id, out var evalTotal) ? evalTotal : 0;

                        var added = result.TerritoryNewAddressCounts.TryGetValue(id, out var addedCount)
                            ? addedCount
                            : result.TerritoryNewAddressCountsEvaluated.TryGetValue(id, out var evalAdded) ? evalAdded : 0;
                        var found = result.TerritoryFoundAddressCounts.TryGetValue(id, out var foundCount)
                            ? foundCount
                            : 0;
                        var distinct = result.TerritoryDistinctAddressCounts.TryGetValue(id, out var distinctCount)
                            ? distinctCount
                            : 0;
                        var existing = result.TerritoryExistingAddressCounts.TryGetValue(id, out var existingCount) ? existingCount : 0;
                        var hasOutput = result.TerritoryOutputFiles.TryGetValue(id, out var path);
                        var hasWarning = total >= effectiveThreshold
                            || result.Warnings.Any(w => w.IndexOf(label, StringComparison.OrdinalIgnoreCase) >= 0 || w.IndexOf(id, StringComparison.OrdinalIgnoreCase) >= 0);
                        var hasConsolidatedOutput = !result.WasWhatIf && result.OutputFilePaths.Count > 0;
                        var isNoneNewTerritory = result.NoneNewTerritoryIds.Contains(id);
                        var consolidatedOnly = !hasOutput && hasConsolidatedOutput && (!isNoneNewTerritory || noneNewConsolidatedValue);

                        var thresholdSuppressed = !outputDespiteThresholdValue && !result.WasWhatIf && !hasOutput && hasWarning;
                        var writtenCount = thresholdSuppressed ? 0 : total;
                        var line = BuildTerritorySummaryLine(label, existing, found, distinct, added, writtenCount);
                        var addfill = result.TerritoryStatePostalBackfilled.TryGetValue(id, out var addfillCount) ? addfillCount : 0;
                        if (addfill > 0)
                        {
                            line += $", addfill {addfill}";
                        }
                        var filled = result.TerritoryCoordinatesBackfilled.TryGetValue(id, out var filledCount) ? filledCount : 0;
                        var overwritten = result.TerritoryCoordinatesOverwritten.TryGetValue(id, out var overwrittenCount) ? overwrittenCount : 0;
                        if (filled > 0 || overwritten > 0)
                        {
                            line += $" | geofill {filled}, over {overwritten}";
                        }
                        if (consolidatedOnly)
                        {
                            line += " | consolidated";
                        }
                        if (hasOutput && !string.IsNullOrEmpty(path))
                        {
                            line += $" | file: {FormatClickableFilePath(path)}";
                        }
                        if (hasWarning)
                        {
                            line += " 🫗";
                        }

                        if (hasWarning)
                        {
                            var originalColor = Console.ForegroundColor;
                            Console.ForegroundColor = ConsoleColor.Yellow;
                            Console.WriteLine(line);
                            Console.ForegroundColor = originalColor;
                        }
                        else
                        {
                            Console.WriteLine(line);
                        }
                    }
                }

                var skippedNoneNewCount = territoryIds.Count(id =>
                {
                    var added = result.TerritoryNewAddressCounts.TryGetValue(id, out var addedCount)
                        ? addedCount
                        : result.TerritoryNewAddressCountsEvaluated.TryGetValue(id, out var evalAdded) ? evalAdded : 0;
                    var hasOutput = result.TerritoryOutputFiles.ContainsKey(id);
                    return added == 0 && !hasOutput;
                });

                var warningTerritoryCount = territoryIds.Count(id =>
                {
                    var total = result.TerritoryAddressCounts.TryGetValue(id, out var totalCount) ? totalCount : 0;
                    var label = result.TerritoryDisplayNames.TryGetValue(id, out var display) ? display : id;
                    var hasThresholdWarning = total >= effectiveThreshold;
                    var hasTextWarning = result.Warnings.Any(w => w.IndexOf(id, StringComparison.OrdinalIgnoreCase) >= 0 || w.IndexOf(label, StringComparison.OrdinalIgnoreCase) >= 0);
                    return hasThresholdWarning || hasTextWarning;
                });
                var skippedExceededThresholdCount = warningTerritoryCount;
                if (result.WasWhatIf)
                {
                    Console.WriteLine("Extraction complete (what-if). No files were written.");
                    Console.WriteLine(outputSplitValue > 0
                        ? $"Planned output: {resolvedOutput} (split every {outputSplitValue} addresses)"
                        : $"Planned output: {resolvedOutput}");

                    if (!string.IsNullOrWhiteSpace(perTerritoryOutputDirectory))
                    {
                        Console.WriteLine($"Per-territory outputs suppressed; planned directory: {perTerritoryOutputDirectory}");
                    }
                }
                else
                {
                    if (result.OutputFilePaths.Count > 1)
                    {
                        Console.WriteLine("Extraction complete. Outputs:");
                        foreach (var path in result.OutputFilePaths)
                        {
                            Console.WriteLine($"  {path}");
                        }
                    }
                    else
                    {
                        Console.WriteLine($"Extraction complete. Output: {result.OutputFilePath}");
                    }

                    if (!string.IsNullOrWhiteSpace(result.NoneNewOutputFilePath))
                    {
                        Console.WriteLine($"None-new output: {result.NoneNewOutputFilePath}");
                    }

                    if (result.CategoryOutputFiles.Count > 0)
                    {
                        Console.WriteLine("Category outputs:");
                        foreach (var categoryPath in result.CategoryOutputFiles.Values.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                        {
                            Console.WriteLine($"  {categoryPath}");
                        }
                    }
                }
                Console.WriteLine(BuildExtractionSummaryLine(result, territoryIds.Count, skippedNoneNewCount, skippedExceededThresholdCount, warningTerritoryCount));
                context.ExitCode = 0;
            }
            catch (InvalidBoundaryException ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                context.ExitCode = 1;
            }
            catch (FileNotFoundException ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                context.ExitCode = 2;
            }
            catch (DirectoryNotFoundException ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                context.ExitCode = 2;
            }
            catch (OperationCanceledException ex)
            {
                Console.Error.WriteLine($"Canceled: {ex.Message}");
                context.ExitCode = 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                context.ExitCode = 2;
            }
        });

        return command;
    }

    private static async Task<IReadOnlyCollection<string>> HandlePreWriteSelectionAsync(
        TerritoryExtractionPlan plan,
        bool addressesFileProvided,
        bool forceWithoutAddressInput,
        bool smartSelect,
        bool selectAll,
        bool noPrompt,
        IProgressRenderer? progressRenderer,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var missingPreExisting = plan.Items.Where(i => !i.HasPreExistingAddresses).ToList();

        if (missingPreExisting.Count > 0 && !forceWithoutAddressInput && !noPrompt)
        {
            if (Console.IsInputRedirected)
            {
                throw new OperationCanceledException("Missing pre-existing addresses for some territories. Re-run with --force-without-address-input to proceed in non-interactive mode.");
            }

            var promptDetail = addressesFileProvided
                ? "No pre-existing rows matched these territories."
                : "No pre-existing addresses file was provided.";

            using var pauseScope = progressRenderer?.Pause();
            Console.WriteLine($"{promptDetail} Proceed without existing addresses for {missingPreExisting.Count} territories? (y/N): ");
            var input = Console.ReadLine();
            if (!string.Equals(input?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
            {
                throw new OperationCanceledException("User declined to proceed without pre-existing addresses.");
            }
        }

        var effectiveSmartSelect = smartSelect || noPrompt;
        var selection = plan.Items.ToDictionary(i => i.TerritoryId, i => selectAll || (effectiveSmartSelect && !i.HasWarning), StringComparer.OrdinalIgnoreCase);

        if (noPrompt)
        {
            return selection.Where(kvp => kvp.Value).Select(kvp => kvp.Key).ToList();
        }

        if (!Console.IsInputRedirected)
        {
            using var pauseScope = progressRenderer?.Pause();
            RunInteractiveSelectionMenu(plan, selection, cancellationToken);
        }

        var selected = selection.Where(kvp => kvp.Value).Select(kvp => kvp.Key).ToList();
        return selected;
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

    private static void RunInteractiveSelectionMenu(
        TerritoryExtractionPlan plan,
        IDictionary<string, bool> selection,
        CancellationToken cancellationToken)
    {
        if (plan.Items.Count == 0)
        {
            return;
        }

        var cursor = 0;
        var pageIndex = 0;
        Console.WriteLine("Use Up/Down to move, Space to toggle, Enter to continue (warning territories default deselected).");
        var bufferHeight = Math.Max(1, Console.BufferHeight);
        var menuTop = Console.CursorTop;
        var minLinesNeeded = Math.Min(plan.Items.Count + 1, bufferHeight);
        if (bufferHeight - menuTop < minLinesNeeded)
        {
            menuTop = Math.Max(0, bufferHeight - minLinesNeeded);
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            bufferHeight = Math.Max(1, Console.BufferHeight);
            var availableLines = Math.Max(2, bufferHeight - menuTop);
            var pageSize = Math.Max(1, availableLines - 1); // leave room for status line
            var totalPages = Math.Max(1, (int)Math.Ceiling(plan.Items.Count / (double)pageSize));
            pageIndex = Math.Min(Math.Max(0, pageIndex), totalPages - 1);

            var pageStart = pageIndex * pageSize;
            var pageEndExclusive = Math.Min(plan.Items.Count, pageStart + pageSize);
            if (cursor < pageStart)
            {
                cursor = pageStart;
            }
            else if (cursor >= pageEndExclusive)
            {
                cursor = pageEndExclusive - 1;
            }

            RenderSelectionMenu(plan, selection, cursor, menuTop, pageIndex, pageSize, totalPages);
            var key = Console.ReadKey(intercept: true);

            switch (key.Key)
            {
                case ConsoleKey.UpArrow:
                    cursor = cursor <= 0 ? plan.Items.Count - 1 : cursor - 1;
                    pageIndex = cursor / Math.Max(1, pageSize);
                    break;
                case ConsoleKey.DownArrow:
                    cursor = cursor >= plan.Items.Count - 1 ? 0 : cursor + 1;
                    pageIndex = cursor / Math.Max(1, pageSize);
                    break;
                case ConsoleKey.LeftArrow:
                case ConsoleKey.PageUp:
                    pageIndex = pageIndex <= 0 ? totalPages - 1 : pageIndex - 1;
                    cursor = Math.Min(plan.Items.Count - 1, pageIndex * pageSize);
                    break;
                case ConsoleKey.RightArrow:
                case ConsoleKey.PageDown:
                    pageIndex = pageIndex >= totalPages - 1 ? 0 : pageIndex + 1;
                    cursor = Math.Min(plan.Items.Count - 1, pageIndex * pageSize);
                    break;
                case ConsoleKey.Spacebar:
                    var id = plan.Items[cursor].TerritoryId;
                    selection[id] = !selection[id];
                    break;
                case ConsoleKey.Enter:
                    var visibleCount = Math.Min(pageSize, plan.Items.Count - (pageIndex * pageSize));
                    Console.SetCursorPosition(0, Math.Min(Console.BufferHeight - 1, menuTop + visibleCount));
                    Console.CursorVisible = true;
                    Console.WriteLine();
                    return;
            }
        }
    }

    private static void RenderSelectionMenu(
        TerritoryExtractionPlan plan,
        IDictionary<string, bool> selection,
        int cursor,
        int top,
        int pageIndex,
        int pageSize,
        int totalPages)
    {
        Console.CursorVisible = false;
        var bufferWidth = Math.Max(1, Console.BufferWidth);
        var bufferHeight = Math.Max(1, Console.BufferHeight);
        var safeTop = Math.Min(Math.Max(0, top), bufferHeight - 1);
        var maxLines = Math.Max(2, bufferHeight - safeTop);

        var pageStart = Math.Min(plan.Items.Count, Math.Max(0, pageIndex * pageSize));
        var remaining = Math.Max(0, plan.Items.Count - pageStart);
        var maxViewHeight = Math.Max(1, maxLines - 1);
        var viewHeight = Math.Min(Math.Max(1, Math.Min(pageSize, remaining)), maxViewHeight);
        var viewStart = pageStart;
        var needsStatusLine = maxLines > viewHeight;

        for (var i = 0; i < viewHeight; i++)
        {
            var itemIndex = viewStart + i;
            if (itemIndex >= plan.Items.Count)
            {
                Console.SetCursorPosition(0, safeTop + i);
                Console.Write(new string(' ', Math.Max(1, bufferWidth - 1)));
                continue;
            }

            var item = plan.Items[itemIndex];
            var isSelected = selection.TryGetValue(item.TerritoryId, out var sel) && sel;
            var marker = isSelected ? "[x]" : "[ ]";
            var warningFlag = item.HasWarning ? " ⚠" : string.Empty;
            var cursorMarker = itemIndex == cursor ? ">" : " ";
            var line = $" {cursorMarker}{marker} {itemIndex + 1}) {item.Label}: existing {item.PreExistingCount}, new {item.AddedCount}, written {item.TotalCount}{warningFlag}";
            if (!string.IsNullOrWhiteSpace(item.ProposedPerTerritoryPath))
            {
                line += $" | file: {item.ProposedPerTerritoryPath}";
            }

            var originalColor = Console.ForegroundColor;
            if (item.HasWarning)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
            }
            else if (itemIndex == cursor)
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
            }

            var padded = line.PadRight(Math.Max(1, bufferWidth - 1));
            Console.SetCursorPosition(0, safeTop + i);
            Console.Write(padded);
            Console.ForegroundColor = originalColor;
        }

        var statusLineWritten = false;
        var statusLineIndex = safeTop + viewHeight;
        if (needsStatusLine && statusLineIndex < bufferHeight)
        {
            var end = Math.Min(viewStart + viewHeight, plan.Items.Count);
            var status = $"Page {pageIndex + 1}/{totalPages} • Showing {viewStart + 1}-{end} of {plan.Items.Count}";
            Console.SetCursorPosition(0, statusLineIndex);
            Console.Write(status.PadRight(Math.Max(1, bufferWidth - 1)));
            statusLineWritten = true;
        }

        var contentLines = viewHeight + (statusLineWritten ? 1 : 0);
        for (var clearIndex = contentLines; clearIndex < maxLines; clearIndex++)
        {
            Console.SetCursorPosition(0, safeTop + clearIndex);
            Console.Write(new string(' ', Math.Max(1, bufferWidth - 1)));
        }

        Console.SetCursorPosition(0, safeTop);
    }

    internal static IReadOnlyList<string> BuildThresholdExceedingLines(ExtractionResult result, int warningThreshold, bool outputExistingAddressesDespiteThreshold = false)
    {
        var effectiveThreshold = warningThreshold <= 0 ? 350 : warningThreshold;
        var evaluatedTotals = result.TerritoryAddressCountsEvaluated.Count > 0
            ? result.TerritoryAddressCountsEvaluated
            : result.TerritoryAddressCounts;

        var evaluatedNewCounts = result.TerritoryNewAddressCountsEvaluated.Count > 0
            ? result.TerritoryNewAddressCountsEvaluated
            : result.TerritoryNewAddressCounts;

        var labels = result.TerritoryDisplayNames;
        var foundCounts = result.TerritoryFoundAddressCounts;
        var distinctCounts = result.TerritoryDistinctAddressCounts;
        var outputFiles = result.TerritoryOutputFiles;
        var metadata = result.TerritoryMetadata;

        var exceeding = evaluatedTotals
            .Where(kvp => kvp.Value > effectiveThreshold)
            .OrderBy(kvp => BuildTerritorySortKey(kvp.Key, result))
            .ThenBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var lines = new List<string>();
        foreach (var kvp in exceeding)
        {
            var label = labels.TryGetValue(kvp.Key, out var display)
                ? display
                : BuildLabelFromMetadata(kvp.Key, metadata);
            var newCount = evaluatedNewCounts.TryGetValue(kvp.Key, out var added) ? added : 0;
            var existing = kvp.Value - newCount;
            if (existing < 0)
            {
                existing = 0;
            }

            var found = foundCounts.TryGetValue(kvp.Key, out var foundValue) ? foundValue : kvp.Value;
            var distinct = distinctCounts.TryGetValue(kvp.Key, out var distinctValue) ? distinctValue : kvp.Value;

            var proposed = kvp.Value;

            lines.Add($"{label}: existing {existing}, found {found}, distinct {distinct}, new {newCount}, prop {proposed}");
        }

        return lines;
    }

    internal static string BuildTerritorySummaryLine(string label, int existing, int found, int distinct, int added, int writtenCount)
    {
        var expectedCount = existing + added;
        var diffCount = writtenCount - expectedCount;
        var line = $"{label}: existing {existing}, found {found}, distinct {distinct}, new {added}, written {writtenCount}";
        if (diffCount != 0)
        {
            var sign = diffCount > 0 ? "+" : "-";
            line += $", ⚠️ count {sign}{Math.Abs(diffCount)}";
        }

        return line;
    }

    internal static string BuildExtractionSummaryLine(
        ExtractionResult result,
        int territoryCount,
        int skippedNoneNewCount,
        int skippedExceededThresholdCount,
        int warningTerritoryCount)
    {
        var writtenLabel = result.WasWhatIf ? "planned" : "written";
        var addfilledLabel = result.StatePostalBackfilled > 0
            ? $", addfilled {result.StatePostalBackfilled}"
            : string.Empty;
        var line = $"Addresses: {result.PreExistingAddressesCount} existing, {result.MatchedAddresses} found, {result.DistinctMatchedAddresses} distinct, {result.NewAddressesCount} new, {result.TotalOutputRows} {writtenLabel}{addfilledLabel}. Territories: total {territoryCount}, skipped {skippedNoneNewCount}NN {skippedExceededThresholdCount}XT, warnings {warningTerritoryCount}";

        if (result.CoordinatesBackfilled > 0 || result.CoordinatesOverwritten > 0)
        {
            line += $" Coordinates: {result.CoordinatesBackfilled} backfilled, {result.CoordinatesOverwritten} overwritten";
        }

        if (result.TotalCappedNewAddresses > 0 && result.AppliedMaxNewAddressesPerTerritory.HasValue)
        {
            line += $" Entitlement cap: {result.TotalCappedNewAddresses} new addresses omitted at max {result.AppliedMaxNewAddressesPerTerritory.Value}/territory.";
        }

        return line;
    }

    private static EntitlementContext BuildCliEntitlementContext()
    {
        var basePlanRaw = Environment.GetEnvironmentVariable("NWSHELPER_ENTITLEMENT_PLAN");
        var addOnsRaw = Environment.GetEnvironmentVariable("NWSHELPER_ENTITLEMENT_ADD_ONS");
        var maxRaw = Environment.GetEnvironmentVariable("NWSHELPER_MAX_NEW_ADDRESSES_PER_TERRITORY");
        var expiresRaw = Environment.GetEnvironmentVariable("NWSHELPER_ENTITLEMENT_EXPIRES_UTC");

        var basePlanCode = string.IsNullOrWhiteSpace(basePlanRaw)
            ? EntitlementProductCodes.FreeBasePlan
            : basePlanRaw.Trim().ToLowerInvariant();

        var addOnCodes = (addOnsRaw ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(code => code.ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        int? maxNewAddresses = null;
        if (int.TryParse(maxRaw, out var parsedMax) && parsedMax > 0)
        {
            maxNewAddresses = parsedMax;
        }

        DateTimeOffset? expiresUtc = null;
        if (DateTimeOffset.TryParse(expiresRaw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedExpires))
        {
            expiresUtc = parsedExpires.ToUniversalTime();
        }

        return new EntitlementContext
        {
            BasePlanCode = basePlanCode,
            AddOnCodes = addOnCodes,
            MaxNewAddressesPerTerritory = maxNewAddresses,
            ExpiresUtc = expiresUtc,
            LastValidatedUtc = DateTimeOffset.UtcNow,
            ValidationSource = "CLIEnvironment"
        };
    }

    private static string BuildLabelFromMetadata(string territoryId, IReadOnlyDictionary<string, TerritoryMetadata> metadata)
    {
        if (metadata.TryGetValue(territoryId, out var meta))
        {
            var category = string.IsNullOrWhiteSpace(meta.CategoryCode) ? "Unknown" : meta.CategoryCode;
            var number = string.IsNullOrWhiteSpace(meta.Number) ? "Unknown" : meta.Number;
            var suffix = string.IsNullOrWhiteSpace(meta.Suffix) ? string.Empty : $"-{meta.Suffix}";
            return $"{category}-{number}{suffix} [ID:{territoryId}]";
        }

        return territoryId;
    }

    private static TerritorySortKey BuildTerritorySortKey(string territoryId, ExtractionResult result)
    {
        var metadata = result.TerritoryMetadata.TryGetValue(territoryId, out var found)
            ? found
            : new TerritoryMetadata(string.Empty, string.Empty, string.Empty);

        return TerritorySortKey.From(metadata, territoryId);
    }

    private sealed class TerritorySortKey : IComparable<TerritorySortKey>
    {
        private TerritorySortKey(string territoryId, string category, string numberText, bool hasNumeric, int numericValue, string suffix)
        {
            TerritoryId = territoryId;
            Category = category;
            NumberText = numberText;
            HasNumeric = hasNumeric;
            NumericValue = numericValue;
            Suffix = suffix;
        }

        private string TerritoryId { get; }
        private string Category { get; }
        private string NumberText { get; }
        private bool HasNumeric { get; }
        private int NumericValue { get; }
        private string Suffix { get; }

        public int CompareTo(TerritorySortKey? other)
        {
            if (other is null)
            {
                return 1;
            }

            var categoryComparison = string.Compare(Category, other.Category, StringComparison.OrdinalIgnoreCase);
            if (categoryComparison != 0)
            {
                return categoryComparison;
            }

            if (HasNumeric || other.HasNumeric)
            {
                if (HasNumeric && other.HasNumeric)
                {
                    var numericComparison = NumericValue.CompareTo(other.NumericValue);
                    if (numericComparison != 0)
                    {
                        return numericComparison;
                    }
                }
                else
                {
                    return HasNumeric ? -1 : 1;
                }
            }

            var textComparison = string.Compare(NumberText, other.NumberText, StringComparison.OrdinalIgnoreCase);
            if (textComparison != 0)
            {
                return textComparison;
            }

            var suffixComparison = string.Compare(Suffix, other.Suffix, StringComparison.OrdinalIgnoreCase);
            if (suffixComparison != 0)
            {
                return suffixComparison;
            }

            return string.Compare(TerritoryId, other.TerritoryId, StringComparison.OrdinalIgnoreCase);
        }

        public static TerritorySortKey From(TerritoryMetadata metadata, string territoryId)
        {
            var category = metadata.CategoryCode?.Trim() ?? string.Empty;
            var numberText = metadata.Number?.Trim() ?? string.Empty;
            var suffix = metadata.Suffix?.Trim() ?? string.Empty;
            var hasNumeric = int.TryParse(numberText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numericValue);

            return new TerritorySortKey(territoryId, category, numberText, hasNumeric, numericValue, suffix);
        }
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

        if (onlyMatchOnSingleCounty)
        {
            if (!allHaveCounty || countySet.Count != 1)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryParseSmartFillApartmentUnitsMode(string? value, out SmartFillApartmentUnitsMode mode)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            mode = SmartFillApartmentUnitsMode.TypeApartmentOnly;
            return true;
        }

        if (Enum.TryParse<SmartFillApartmentUnitsMode>(value.Trim(), ignoreCase: true, out var parsed))
        {
            mode = parsed;
            return true;
        }

        mode = SmartFillApartmentUnitsMode.TypeApartmentOnly;
        return false;
    }

    /// <summary>
    /// Formats a file path as a clickable hyperlink showing only the filename.
    /// Uses OSC 8 hyperlink escape sequences supported by modern terminals.
    /// </summary>
    private static string FormatClickableFilePath(string fullPath)
    {
        var fileName = Path.GetFileName(fullPath);
        var fileUri = new Uri(Path.GetFullPath(fullPath)).AbsoluteUri;
        
        // OSC 8 hyperlink format: \e]8;;URI\e\\TEXT\e]8;;\e\\
        // Some terminals support this for clickable links
        return $"\x1b]8;;{fileUri}\x1b\\{fileName}\x1b]8;;\x1b\\";
    }
}

