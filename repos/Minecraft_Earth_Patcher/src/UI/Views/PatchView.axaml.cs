using Avalonia.Controls;
using Avalonia.Interactivity;
using MCEPatcher.Core;
using MCEPatcher.UI.ViewModels;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace MCEPatcher.UI.Views;

public partial class PatchView : UserControl
{
    public PatchView()
    {
        InitializeComponent();
        PatchViewModel model = new PatchViewModel();
        DataContext = model;
    }

    public void Patch(ApkProcessor.Options options)
    {
        (DataContext as PatchViewModel)?.Start(options, scrollViewer, chat, finishedContainer);
    }

    public void Patch(IpaProcessor.Options options)
    {
        (DataContext as PatchViewModel)?.Start(options, scrollViewer, chat, finishedContainer);
    }

    public void Back(object sender, RoutedEventArgs args)
    {
        MainWindow.Instance.OpenMainView();
    }

    public void OpenPatchedAPKLocation(object sender, RoutedEventArgs args)
    {
        OpenFileLocation(Path.GetFullPath("Solace.apk"));
    }

    public void OpenPatchedIPALocation(object sender, RoutedEventArgs args)
    {
        OpenFileLocation(Path.GetFullPath("Solace.ipa"));
    }

    private static void OpenFileLocation(string filePath)
    {
        if (!File.Exists(filePath))
            return;

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start("explorer.exe", $"/select,\"{filePath}\"");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                Process.Start("xdg-open", Path.GetDirectoryName(filePath));
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start("open", $"-R \"{filePath}\"");
            }
        }
        catch { }
    }
}
