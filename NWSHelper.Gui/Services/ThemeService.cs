using System;
using Avalonia;
using Avalonia.Styling;

namespace NWSHelper.Gui.Services;

public enum AppThemePreference
{
    System,
    Light,
    Dark
}

public interface IThemeService
{
    AppThemePreference CurrentPreference { get; }

    void ApplyTheme(AppThemePreference preference);
}

public sealed class ThemeService : IThemeService
{
    private readonly GuiConfigurationStore configurationStore;

    public ThemeService(string? filePath = null)
    {
        configurationStore = new GuiConfigurationStore(filePath);

        CurrentPreference = LoadPreference();
        ApplyTheme(CurrentPreference);
    }

    public AppThemePreference CurrentPreference { get; private set; }

    public void ApplyTheme(AppThemePreference preference)
    {
        CurrentPreference = preference;

        if (Application.Current is not null)
        {
            Application.Current.RequestedThemeVariant = preference switch
            {
                AppThemePreference.Light => ThemeVariant.Light,
                AppThemePreference.Dark => ThemeVariant.Dark,
                _ => ThemeVariant.Default
            };
        }

        SavePreference(preference);
    }

    private AppThemePreference LoadPreference()
    {
        try
        {
            return configurationStore.Load().Theme;
        }
        catch
        {
            return AppThemePreference.System;
        }
    }

    private void SavePreference(AppThemePreference preference)
    {
        try
        {
            var settings = configurationStore.Load();
            settings.Theme = preference;
            configurationStore.Save(settings);
        }
        catch
        {
        }
    }
}
