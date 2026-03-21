using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System;
using System.Linq;
using Avalonia.Markup.Xaml;
using NWSHelper.Gui.Services;
using NWSHelper.Gui.ViewModels;
using NWSHelper.Gui.Views;

namespace NWSHelper.Gui;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

#if DEBUG
        this.AttachDeveloperTools();
#endif
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Avoid duplicate validations from both Avalonia and the CommunityToolkit. 
            // More info: https://docs.avaloniaui.net/docs/guides/development-guides/data-validation#manage-validationplugins
            DisableAvaloniaDataAnnotationValidation();
            var themeService = new ThemeService();
            var setupSettingsService = new SetupSettingsService();
            var storeRuntimeContextProvider = new StoreRuntimeContextProvider();
            var accountLinkService = new AccountLinkService(storeRuntimeContextProvider: storeRuntimeContextProvider);
            var updateService = new NetSparkleUpdateService(storeRuntimeContextProvider: storeRuntimeContextProvider);
            var launchOptions = GuiLaunchArgumentsParser.Parse(desktop.Args ?? Array.Empty<string>());

            desktop.Exit += (_, _) =>
            {
                updateService.Dispose();
            };

            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(
                    new ExtractionOrchestrator(),
                    new OutputPathActions(),
                    themeService,
                    setupSettingsService,
                    launchOptions,
                    null,
                    null,
                    accountLinkService,
                    updateService),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void DisableAvaloniaDataAnnotationValidation()
    {
        // Get an array of plugins to remove
        var dataValidationPluginsToRemove =
            BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

        // remove each entry found
        foreach (var plugin in dataValidationPluginsToRemove)
        {
            BindingPlugins.DataValidators.Remove(plugin);
        }
    }
}
