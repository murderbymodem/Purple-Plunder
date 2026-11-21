using System;
using System.Collections.Generic;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Ninject;
using PurplePlunder.Gui.Modules;
using PurplePlunder.Gui.ViewModels;
using PurplePlunder.Gui.Views;
using PurplePlunder.Services.Interfaces;
using PurplePlunder.Services.Modules;
using PurplePlunder.Services.Services;
using PurplePlunder.Shared.Communication;
using PurplePlunder.Shared.Events;

namespace PurplePlunder;

public partial class App : Application
{
    private readonly Mutex _singletonMutex;

    private IKernel _kernel;
    private Dictionary<string, ResourceDictionary> _themes;
    private ResourceDictionary _sharedResources;

    public App()
    {
        _themes = new Dictionary<string, ResourceDictionary>();
        _singletonMutex = new Mutex(true, "PurplePlunder", out bool isOnlyInstance);

        if (!isOnlyInstance)
        {
            new NamedPipeManager("PurplePlunder").Write("Activate");

            Environment.Exit(0);
        }
    }

    private void OnExit(object sender,
        ControlledApplicationLifetimeExitEventArgs controlledApplicationLifetimeExitEventArgs)
    {
        if (_singletonMutex != null)
        {
            _singletonMutex.Dispose();
        }
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private IKernel CreateKernel()
    {
        IKernel kernel = new StandardKernel();

        RegisterTypes(kernel);
        LoadModules(kernel);

        return kernel;
    }

    private void RegisterTypes(IKernel kernel)
    {
        kernel.Bind<MainWindow>().ToSelf().InSingletonScope();
        kernel.Bind<MainWindowVM>().ToSelf().InSingletonScope();
        kernel.Bind<IEventAggregator>().To<EventAggregator>().InSingletonScope();
    }

    private void LoadModules(IKernel kernel)
    {
        kernel.Load<GuiModule>();
        kernel.Load<ServiceModule>();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        _kernel = CreateKernel();

        _sharedResources = (ResourceDictionary)Resources["Shared"];
        _themes.Add("New", (ResourceDictionary)Resources["New"]);
        _themes.Add("Original", (ResourceDictionary)Resources["Original"]);
        var themeService = _kernel.Get<IThemeService>();
        themeService.StyleChanged += (sender, args) => { SetTheme(themeService.GetTheme()); };
        var preferencesService = _kernel.Get<IPreferencesService>();
        SetTheme(string.IsNullOrEmpty(preferencesService.CurrentPreferences.Theme)
            ? "New"
            : preferencesService.CurrentPreferences.Theme);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Exit += OnExit;
            desktop.MainWindow = _kernel.Get<MainWindow>();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void SetTheme(string name)
    {
        Resources.Clear();

        foreach (var sharedResouce in _sharedResources)
        {
            Resources.Add(sharedResouce);
        }

        foreach (var themeResource in _themes[name])
        {
            Resources.Add(themeResource);
        }
    }
}