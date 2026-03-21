using System;
using System.IO;
using System.Text.Json;

namespace NWSHelper.Gui.Services;

public sealed class GuiConfigurationDocument
{
    public AppThemePreference Theme { get; set; } = AppThemePreference.System;

    public GuiSetupSettings? Setup { get; set; }

    public GuiEntitlementSettings? Entitlement { get; set; }

    public GuiUpdateSettings? Updates { get; set; }
}

public sealed class GuiConfigurationStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private const string UnifiedSettingsFileName = "gui-settings.json";
    private const string LegacyThemeSettingsFileName = "ui-settings.json";
    private const string LegacySetupSettingsFileName = "gui-setup-settings.json";

    private readonly string settingsPath;
    private readonly string? legacyThemeSettingsPath;
    private readonly string? legacySetupSettingsPath;

    public GuiConfigurationStore(string? filePath = null)
    {
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            settingsPath = filePath;
        }
        else
        {
            var appDataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NWSHelper");
            Directory.CreateDirectory(appDataDirectory);
            settingsPath = Path.Combine(appDataDirectory, UnifiedSettingsFileName);
        }

        var directory = Path.GetDirectoryName(settingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            legacyThemeSettingsPath = Path.Combine(directory, LegacyThemeSettingsFileName);
            legacySetupSettingsPath = Path.Combine(directory, LegacySetupSettingsFileName);
        }
    }

    public GuiConfigurationDocument Load()
    {
        var unified = TryLoadUnified();
        if (unified is not null)
        {
            return unified;
        }

        return TryLoadLegacy();
    }

    public void Save(GuiConfigurationDocument settings)
    {
        try
        {
            var directory = Path.GetDirectoryName(settingsPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempPath = settingsPath + ".tmp";
            var json = JsonSerializer.Serialize(settings, SerializerOptions);
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, settingsPath, overwrite: true);
        }
        catch
        {
        }
    }

    private GuiConfigurationDocument? TryLoadUnified()
    {
        try
        {
            if (!File.Exists(settingsPath))
            {
                return null;
            }

            var json = File.ReadAllText(settingsPath);
            var settings = JsonSerializer.Deserialize<GuiConfigurationDocument>(json);
            if (settings is null)
            {
                return new GuiConfigurationDocument();
            }

            return settings;
        }
        catch
        {
            return null;
        }
    }

    private GuiConfigurationDocument TryLoadLegacy()
    {
        var settings = new GuiConfigurationDocument();
        var hasLegacyValues = false;

        if (!string.IsNullOrWhiteSpace(legacyThemeSettingsPath) && File.Exists(legacyThemeSettingsPath))
        {
            try
            {
                var json = File.ReadAllText(legacyThemeSettingsPath);
                var legacyTheme = JsonSerializer.Deserialize<LegacyThemeSettings>(json);
                if (legacyTheme is not null)
                {
                    settings.Theme = legacyTheme.Theme;
                    hasLegacyValues = true;
                }
            }
            catch
            {
            }
        }

        if (!string.IsNullOrWhiteSpace(legacySetupSettingsPath) && File.Exists(legacySetupSettingsPath))
        {
            try
            {
                var json = File.ReadAllText(legacySetupSettingsPath);
                var legacySetup = JsonSerializer.Deserialize<GuiSetupSettings>(json);
                if (legacySetup is not null)
                {
                    settings.Setup = legacySetup;
                    hasLegacyValues = true;
                }
            }
            catch
            {
            }
        }

        if (hasLegacyValues)
        {
            Save(settings);
            DeleteFileIfExists(legacyThemeSettingsPath);
            DeleteFileIfExists(legacySetupSettingsPath);
        }

        return settings;
    }

    private static void DeleteFileIfExists(string? path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private sealed class LegacyThemeSettings
    {
        public AppThemePreference Theme { get; set; } = AppThemePreference.System;
    }
}
