using Avalonia.Controls;
using MCEPatcher.Core;
using MCEPatcher.UI.ViewModels;

namespace MCEPatcher.UI.Views;

public partial class MainWindow : Window
{
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    internal static MainWindow Instance { get; private set; }
#pragma warning restore CS8618

    public MainWindow()
    {
        Instance = this;

        Width = 600;
        Height = 600;

        //CanResize = false;

        InitializeComponent();
    }

    public void OpenMainView()
    {
        ((Content as UserControl)?.DataContext as ViewModelBase)?.OnClose();
        Content = new MainView();
    }

    public void Patch(ApkProcessor.Options options)
    {
        ((Content as UserControl)?.DataContext as ViewModelBase)?.OnClose();
        PatchView view = new PatchView();
        view.Patch(options);
        Content = view;
    }

    public void Patch(IpaProcessor.Options options)
    {
        ((Content as UserControl)?.DataContext as ViewModelBase)?.OnClose();
        PatchView view = new PatchView();
        view.Patch(options);
        Content = view;
    }
}
