using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MCEPatcher.UI.Utils;
using MCEPatcher.UI.ViewModels;
using MCEPatcher.UI.Views;
using Serilog;
using System;
using System.Text;

namespace MCEPatcher.UI;

public partial class App : Application
{
    internal static event Action<string?>? OnLogWritten;

    public override void Initialize()
    {
        StringWriterExt writer = new StringWriterExt()
        {
            NewLine = "\n"
        };
        StringBuilder sb = new StringBuilder();
        writer.OnWrite += (object sender, StringWriterExt.OnWriteEventArgs args) =>
        {
            sb.Append(args.Value);
            string str = sb.ToString();
            int index = str.IndexOf('\n');
            if (index is not -1)
            {
                OnLogWritten?.Invoke(str.Substring(0, index));
                sb = new StringBuilder(str.Substring(index + 1));
            }
        };

        var log = new LoggerConfiguration()
           .WriteTo.TextWriter(writer, outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
           .WriteTo.File("logs/debug.txt", rollingInterval: RollingInterval.Day, rollOnFileSizeLimit: true, fileSizeLimitBytes: 8338607, outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
           .MinimumLevel.Debug()
           .CreateLogger();
        Log.Logger = log;

        AppDomain.CurrentDomain.UnhandledException += (object sender, UnhandledExceptionEventArgs e) =>
        {
            Log.Fatal($"Unhandled exception: {e.ExceptionObject}");
            Log.CloseAndFlush();
            Environment.Exit(1);
        };

        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainViewModel()
            };
        }
        //else if (ApplicationLifetime is ISingleViewApplicationLifetime singleViewPlatform)
        //{
        //    singleViewPlatform.MainView = new MainView
        //    {
        //        DataContext = new MainViewModel()
        //    };
        //}

        base.OnFrameworkInitializationCompleted();
    }
}
