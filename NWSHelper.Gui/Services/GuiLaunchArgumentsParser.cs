using System;
using NWSHelper.Core.Models;

namespace NWSHelper.Gui.Services;

public sealed class GuiLaunchOptions
{
    public string? BoundaryCsvPath { get; init; }

    public string? ExistingAddressesCsvPath { get; init; }

    public string? StatesFilter { get; init; }

    public string? DatasetRootPath { get; init; }

    public string? ConsolidatedOutputPath { get; init; }

    public int? OutputSplitRows { get; init; }

    public int? WarningThreshold { get; init; }

    public bool? PerTerritoryOutput { get; init; }

    public bool? ForceWithoutAddressInput { get; init; }

    public bool? SmartSelect { get; init; }

    public bool? SelectAll { get; init; }

    public bool? NoPrompt { get; init; }

    public bool? WhatIf { get; init; }

    public bool? OutputDespiteThreshold { get; init; }

    public bool? OutputExistingNoneNew { get; init; }

    public bool? GroupByCategory { get; init; }

    public bool? NoneNewInConsolidated { get; init; }

    public bool? OutputAllRows { get; init; }

    public bool? ExcludeNormalizedRows { get; init; }

    public bool? OverwriteExistingLatLong { get; init; }

    public bool? OnlyMatchSingleState { get; init; }

    public bool? OnlyMatchSingleCounty { get; init; }

    public bool? PreserveRawState { get; init; }

    public bool? PreserveRawStreet { get; init; }

    public bool? SmartFillApartmentUnits { get; init; }

    public string? SmartFillApartmentUnitsMode { get; init; }

    public bool? ListThresholdExceeding { get; init; }

    public bool HasAnyOverrides =>
        !string.IsNullOrWhiteSpace(BoundaryCsvPath) ||
        ExistingAddressesCsvPath is not null ||
        !string.IsNullOrWhiteSpace(StatesFilter) ||
        !string.IsNullOrWhiteSpace(DatasetRootPath) ||
        !string.IsNullOrWhiteSpace(ConsolidatedOutputPath) ||
        OutputSplitRows.HasValue ||
        WarningThreshold.HasValue ||
        PerTerritoryOutput.HasValue ||
        ForceWithoutAddressInput.HasValue ||
        SmartSelect.HasValue ||
        SelectAll.HasValue ||
        NoPrompt.HasValue ||
        WhatIf.HasValue ||
        OutputDespiteThreshold.HasValue ||
        OutputExistingNoneNew.HasValue ||
        GroupByCategory.HasValue ||
        NoneNewInConsolidated.HasValue ||
        OutputAllRows.HasValue ||
        ExcludeNormalizedRows.HasValue ||
        OverwriteExistingLatLong.HasValue ||
        OnlyMatchSingleState.HasValue ||
        OnlyMatchSingleCounty.HasValue ||
        PreserveRawState.HasValue ||
        PreserveRawStreet.HasValue ||
        SmartFillApartmentUnits.HasValue ||
        !string.IsNullOrWhiteSpace(SmartFillApartmentUnitsMode) ||
        ListThresholdExceeding.HasValue;
}

public static class GuiLaunchArgumentsParser
{
    public static GuiLaunchOptions Parse(string[]? args)
    {
        if (args is null || args.Length == 0)
        {
            return new GuiLaunchOptions();
        }

        var options = new MutableGuiLaunchOptions();

        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (string.Equals(token, "extract", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var optionName = token;
            string? inlineValue = null;
            var equalsIndex = token.IndexOf('=');
            if (equalsIndex > 0)
            {
                optionName = token[..equalsIndex];
                inlineValue = token[(equalsIndex + 1)..];
            }

            switch (optionName.ToLowerInvariant())
            {
                case "--boundary-csv":
                    if (TryReadStringValue(args, ref i, inlineValue, out var boundaryCsv))
                    {
                        options.BoundaryCsvPath = boundaryCsv;
                    }
                    break;

                case "--addresses-csv":
                    if (TryReadStringValue(args, ref i, inlineValue, out var addressesCsv))
                    {
                        options.ExistingAddressesCsvPath = addressesCsv;
                    }
                    break;

                case "--states":
                    if (TryReadStringValue(args, ref i, inlineValue, out var statesCsv))
                    {
                        options.StatesFilter = statesCsv;
                    }
                    break;

                case "--dataset-root":
                    if (TryReadStringValue(args, ref i, inlineValue, out var datasetRoot))
                    {
                        options.DatasetRootPath = datasetRoot;
                    }
                    break;

                case "--out":
                    if (TryReadStringValue(args, ref i, inlineValue, out var outputPath))
                    {
                        options.ConsolidatedOutputPath = outputPath;
                    }
                    break;

                case "--output-split":
                    if (TryReadStringValue(args, ref i, inlineValue, out var outputSplit) && int.TryParse(outputSplit, out var splitValue))
                    {
                        options.OutputSplitRows = splitValue;
                    }
                    break;

                case "--warning-threshold":
                    if (TryReadStringValue(args, ref i, inlineValue, out var warningThreshold) && int.TryParse(warningThreshold, out var thresholdValue))
                    {
                        options.WarningThreshold = thresholdValue;
                    }
                    break;

                case "--per-territory-output":
                case "--per-territory":
                    options.PerTerritoryOutput = ReadSwitchValue(args, ref i, inlineValue);
                    break;

                case "--force-without-address-input":
                    options.ForceWithoutAddressInput = ReadSwitchValue(args, ref i, inlineValue);
                    break;

                case "--smart-select":
                    options.SmartSelect = ReadSwitchValue(args, ref i, inlineValue);
                    break;

                case "--select-all":
                    options.SelectAll = ReadSwitchValue(args, ref i, inlineValue);
                    break;

                case "--no-prompt":
                    options.NoPrompt = ReadSwitchValue(args, ref i, inlineValue);
                    break;

                case "--what-if":
                    options.WhatIf = ReadSwitchValue(args, ref i, inlineValue);
                    break;

                case "--output-existing-addresses-despite-threshold":
                    options.OutputDespiteThreshold = ReadSwitchValue(args, ref i, inlineValue);
                    break;

                case "--output-existing-none-new":
                    options.OutputExistingNoneNew = ReadSwitchValue(args, ref i, inlineValue);
                    break;

                case "--group-by-category":
                    options.GroupByCategory = ReadSwitchValue(args, ref i, inlineValue);
                    break;

                case "--nonenew-consolidated":
                    options.NoneNewInConsolidated = ReadSwitchValue(args, ref i, inlineValue);
                    break;

                case "--output-all-rows":
                    options.OutputAllRows = ReadSwitchValue(args, ref i, inlineValue);
                    break;

                case "--exclude-normalized-rows":
                    options.ExcludeNormalizedRows = ReadSwitchValue(args, ref i, inlineValue);
                    break;

                case "--overwrite-existing-latlong":
                    options.OverwriteExistingLatLong = ReadSwitchValue(args, ref i, inlineValue);
                    break;

                case "--only-match-on-single-state":
                    options.OnlyMatchSingleState = ReadSwitchValue(args, ref i, inlineValue);
                    break;

                case "--only-match-on-single-county":
                    options.OnlyMatchSingleCounty = ReadSwitchValue(args, ref i, inlineValue);
                    break;

                case "--nonormalize-state":
                    options.PreserveRawState = ReadSwitchValue(args, ref i, inlineValue);
                    break;

                case "--nonormalize-street":
                    options.PreserveRawStreet = ReadSwitchValue(args, ref i, inlineValue);
                    break;

                case "--smart-fill-apartment-units":
                    options.SmartFillApartmentUnits = true;
                    if (TryReadStringValue(args, ref i, inlineValue, out var smartFillMode) && TryParseSmartFillMode(smartFillMode, out var parsedMode))
                    {
                        options.SmartFillApartmentUnitsMode = parsedMode;
                    }
                    break;

                case "--list-threshold-exceeding":
                    options.ListThresholdExceeding = ReadSwitchValue(args, ref i, inlineValue);
                    break;
            }
        }

        return options.ToImmutable();
    }

    private static bool TryReadStringValue(string[] args, ref int index, string? inlineValue, out string? value)
    {
        if (inlineValue is not null)
        {
            value = inlineValue;
            return true;
        }

        if (index + 1 < args.Length && !IsOptionToken(args[index + 1]))
        {
            value = args[++index];
            return true;
        }

        value = null;
        return false;
    }

    private static bool ReadSwitchValue(string[] args, ref int index, string? inlineValue)
    {
        if (TryParseBoolean(inlineValue, out var inlineParsed))
        {
            return inlineParsed;
        }

        if (index + 1 < args.Length && TryParseBoolean(args[index + 1], out var nextParsed))
        {
            index++;
            return nextParsed;
        }

        return true;
    }

    private static bool TryParseBoolean(string? value, out bool parsed)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            parsed = false;
            return false;
        }

        if (bool.TryParse(value, out parsed))
        {
            return true;
        }

        if (string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase))
        {
            parsed = true;
            return true;
        }

        if (string.Equals(value, "0", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "no", StringComparison.OrdinalIgnoreCase))
        {
            parsed = false;
            return true;
        }

        return false;
    }

    private static bool TryParseSmartFillMode(string? modeValue, out string parsedMode)
    {
        parsedMode = SmartFillApartmentUnitsMode.TypeApartmentOnly.ToString();
        if (string.IsNullOrWhiteSpace(modeValue))
        {
            return false;
        }

        if (!Enum.TryParse<SmartFillApartmentUnitsMode>(modeValue, ignoreCase: true, out var mode))
        {
            return false;
        }

        parsedMode = mode.ToString();
        return true;
    }

    private static bool IsOptionToken(string value) => value.StartsWith("--", StringComparison.Ordinal);

    private sealed class MutableGuiLaunchOptions
    {
        public string? BoundaryCsvPath { get; set; }

        public string? ExistingAddressesCsvPath { get; set; }

        public string? StatesFilter { get; set; }

        public string? DatasetRootPath { get; set; }

        public string? ConsolidatedOutputPath { get; set; }

        public int? OutputSplitRows { get; set; }

        public int? WarningThreshold { get; set; }

        public bool? PerTerritoryOutput { get; set; }

        public bool? ForceWithoutAddressInput { get; set; }

        public bool? SmartSelect { get; set; }

        public bool? SelectAll { get; set; }

        public bool? NoPrompt { get; set; }

        public bool? WhatIf { get; set; }

        public bool? OutputDespiteThreshold { get; set; }

        public bool? OutputExistingNoneNew { get; set; }

        public bool? GroupByCategory { get; set; }

        public bool? NoneNewInConsolidated { get; set; }

        public bool? OutputAllRows { get; set; }

        public bool? ExcludeNormalizedRows { get; set; }

        public bool? OverwriteExistingLatLong { get; set; }

        public bool? OnlyMatchSingleState { get; set; }

        public bool? OnlyMatchSingleCounty { get; set; }

        public bool? PreserveRawState { get; set; }

        public bool? PreserveRawStreet { get; set; }

        public bool? SmartFillApartmentUnits { get; set; }

        public string? SmartFillApartmentUnitsMode { get; set; }

        public bool? ListThresholdExceeding { get; set; }

        public GuiLaunchOptions ToImmutable() => new()
        {
            BoundaryCsvPath = BoundaryCsvPath,
            ExistingAddressesCsvPath = ExistingAddressesCsvPath,
            StatesFilter = StatesFilter,
            DatasetRootPath = DatasetRootPath,
            ConsolidatedOutputPath = ConsolidatedOutputPath,
            OutputSplitRows = OutputSplitRows,
            WarningThreshold = WarningThreshold,
            PerTerritoryOutput = PerTerritoryOutput,
            ForceWithoutAddressInput = ForceWithoutAddressInput,
            SmartSelect = SmartSelect,
            SelectAll = SelectAll,
            NoPrompt = NoPrompt,
            WhatIf = WhatIf,
            OutputDespiteThreshold = OutputDespiteThreshold,
            OutputExistingNoneNew = OutputExistingNoneNew,
            GroupByCategory = GroupByCategory,
            NoneNewInConsolidated = NoneNewInConsolidated,
            OutputAllRows = OutputAllRows,
            ExcludeNormalizedRows = ExcludeNormalizedRows,
            OverwriteExistingLatLong = OverwriteExistingLatLong,
            OnlyMatchSingleState = OnlyMatchSingleState,
            OnlyMatchSingleCounty = OnlyMatchSingleCounty,
            PreserveRawState = PreserveRawState,
            PreserveRawStreet = PreserveRawStreet,
            SmartFillApartmentUnits = SmartFillApartmentUnits,
            SmartFillApartmentUnitsMode = SmartFillApartmentUnitsMode,
            ListThresholdExceeding = ListThresholdExceeding
        };
    }
}

