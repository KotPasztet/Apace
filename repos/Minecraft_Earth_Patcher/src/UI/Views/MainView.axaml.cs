using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MCEPatcher.UI.Utils;
using MCEPatcher.UI.ViewModels;
using MsBox.Avalonia;
using System.IO;
using System.Linq;
using System.Net;

namespace MCEPatcher.UI.Views;

public partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();
    }

    public async void Patch(object sender, RoutedEventArgs args)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        if (viewModel.SelectedTabIndex is 1)
        {
            string? ipaFile = viewModel.IpaFilePath;

            if (ipaFile is null)
            {
                await U.ShowError("Select the IPA file first");
                return;
            }

            if (!File.Exists(ipaFile))
            {
                await U.ShowError("Selected file doesn't exist");
                return;
            }

            using (var fs = File.OpenRead(ipaFile))
            {
                if (!Core.IpaProcessor.VerifyHash(fs))
                {
                    var dialog = MessageBoxManager.GetMessageBoxStandard(
                        title: "Warning",
                        text: "The .ipa file hash does not match. Patching may fail. Do you want to continue?",
                        @enum: MsBox.Avalonia.Enums.ButtonEnum.YesNo,
                        icon: MsBox.Avalonia.Enums.Icon.Error,
                        windowStartupLocation: WindowStartupLocation.CenterOwner);

                    var result = await dialog.ShowWindowDialogAsync(MainWindow.Instance);

                    if (result is MsBox.Avalonia.Enums.ButtonResult.No)
                    {
                        return;
                    }
                }
            }

            if (viewModel.IosChangeLocatorAddress && string.IsNullOrWhiteSpace(viewModel.IosLocatorHostname))
            {
                await U.ShowError("Enter a locator hostname/IP first");
                return;
            }

            bool customAuthServer = !viewModel.IosIsSimpleMode || viewModel.IosUseCustomAuthServer;
            bool changeXalAuthAddress = customAuthServer && viewModel.IosChangeXalAuthAddress;
            bool changePlayfabApiAddress = customAuthServer && viewModel.IosChangePlayfabApiAddress;
            bool changeXboxABAddress = customAuthServer && viewModel.IosChangeXboxABAddress;
            bool changeXboxLiveAddress = customAuthServer && viewModel.IosChangeXboxLiveAddress;

            if (customAuthServer)
            {
                if (viewModel.IosLoginServerSingleDomainMode)
                {
                    if ((changeXalAuthAddress || changePlayfabApiAddress || changeXboxABAddress || changeXboxLiveAddress)
                        && string.IsNullOrWhiteSpace(viewModel.IosHostname))
                    {
                        await U.ShowError("Enter an auth server hostname/IP first");
                        return;
                    }
                }
                else
                {
                    if (changeXalAuthAddress && string.IsNullOrWhiteSpace(viewModel.IosXalHostname))
                    {
                        await U.ShowError("Enter a Xal auth hostname/IP first");
                        return;
                    }
                    if (changePlayfabApiAddress && string.IsNullOrWhiteSpace(viewModel.IosPlayfabApiHostname))
                    {
                        await U.ShowError("Enter a PlayFab API hostname/IP first");
                        return;
                    }
                    if (changeXboxABAddress && string.IsNullOrWhiteSpace(viewModel.IosXboxABHostname))
                    {
                        await U.ShowError("Enter an XboxAB hostname/IP first");
                        return;
                    }
                    if (changeXboxLiveAddress && string.IsNullOrWhiteSpace(viewModel.IosXboxLiveHostname))
                    {
                        await U.ShowError("Enter an Xbox live hostname/IP first");
                        return;
                    }
                }
            }

            var ipaOptions = new Core.IpaProcessor.Options()
            {
                Autonomous = true,
                InIpa = ipaFile,
                OutIpa = "Solace.ipa",
                DecodedDir = "IpaDecoded",
                Protocol = viewModel.IosProtocol,
                Hostname = viewModel.IosHostname,
                LocatorProtocol = viewModel.IosLocatorProtocol,
                LocatorHostname = viewModel.IosLocatorHostname,
                AppName = viewModel.IosAppName,
                ChangeLocatorAddress = viewModel.IosChangeLocatorAddress,
                ChangeXalAuthAddress = changeXalAuthAddress,
                ForceInAppWebView = customAuthServer && viewModel.IosForceInAppWebView,
                ForceInteractiveSignIn = viewModel.IosForceInteractiveSignIn,
                ChangePlayfabApiAddress = changePlayfabApiAddress,
                ChangeXboxABAddress = changeXboxABAddress,
                ChangeXboxLiveAddress = changeXboxLiveAddress,
                DisableSunsetTimeCheck = viewModel.IosDisableSunsetTimeCheck,
                DisableSpeedLimit = viewModel.IosDisableSpeedLimit,
                ChangeAppName = viewModel.IosChangeAppName,
                ChangeIcon = viewModel.IosChangeIcon && !string.IsNullOrEmpty(viewModel.IconFilePath),
                IconPath = viewModel.IosChangeIcon ? (viewModel.IconFilePath ?? "") : "",
                RemoveDrm = viewModel.IosRemoveDrm,
            };

            if (!viewModel.IosLoginServerSingleDomainMode)
            {
                ipaOptions.XalProtocol = viewModel.IosXalProtocol;
                ipaOptions.XalHostname = viewModel.IosXalHostname;
                ipaOptions.PlayfabApiProtocol = viewModel.IosPlayfabApiProtocol;
                ipaOptions.PlayfabApiHostname = viewModel.IosPlayfabApiHostname;
                ipaOptions.XboxABProtocol = viewModel.IosXboxABProtocol;
                ipaOptions.XboxABHostname = viewModel.IosXboxABHostname;
                ipaOptions.XboxLiveProtocol = viewModel.IosXboxLiveProtocol;
                ipaOptions.XboxLiveHostname = viewModel.IosXboxLiveHostname;
            }

            MainWindow.Instance.Patch(ipaOptions);
            return;
        }

        string? apkFile = viewModel.ApkFilePath;

        if (apkFile is null)
        {
            await U.ShowError("Select the apk file first");
            return;
        }

        if (!File.Exists(apkFile))
        {
            await U.ShowError("Selected apk file doesn't exist");
            return;
        }

        using (var fs = File.OpenRead(apkFile))
        {
            if (!Core.ApkProcessor.VerifyApkHash(fs))
            {
                var dialog = MessageBoxManager.GetMessageBoxStandard(
                    title: "Warning",
                    text: "The .apk file hash does not match. Patching may fail. Do you want to continue?",
                    @enum: MsBox.Avalonia.Enums.ButtonEnum.YesNo,
                    icon: MsBox.Avalonia.Enums.Icon.Error,
                    windowStartupLocation: WindowStartupLocation.CenterOwner);

                var result = await dialog.ShowWindowDialogAsync(MainWindow.Instance);

                if (result is MsBox.Avalonia.Enums.ButtonResult.No)
                {
                    return;
                }
            }
        }

        string? resourcePackFile = viewModel.ResourcePackFilePath;

        if (!string.IsNullOrEmpty(resourcePackFile))
        {
            if (!File.Exists(resourcePackFile))
            {
                await U.ShowError("Selected resource pack file doesn't exist");
                return;
            }

            using (var fs = File.OpenRead(resourcePackFile))
            {
                if (!Core.ApkProcessor.VerifyResourcePackHash(fs))
                {
                    var dialog = MessageBoxManager.GetMessageBoxStandard(
                        title: "Warning",
                        text: "The resource pack file hash does not match. Game may not function correctly. Do you want to continue?",
                        @enum: MsBox.Avalonia.Enums.ButtonEnum.YesNo,
                        icon: MsBox.Avalonia.Enums.Icon.Error,
                        windowStartupLocation: WindowStartupLocation.CenterOwner);

                    var result = await dialog.ShowWindowDialogAsync(MainWindow.Instance);

                    if (result is MsBox.Avalonia.Enums.ButtonResult.No)
                    {
                        return;
                    }
                }
            }
        }

        if (viewModel.AndroidVersion is null or < 7)
        {
            await U.ShowError("Android version must be selected");
            return;
        }

        if (viewModel.ChangeMSALoginServiceAddress && IPAddress.TryParse(viewModel.MSALoginServiceHostname.Split(':')[0], out _))
        {
            await U.ShowError("MSA login service address cannot be an IP");
            return;
        }

        if (viewModel.ChangePlayfabApiAddress && IPAddress.TryParse(viewModel.PlayfabApiHostname.Split(':')[0], out _))
        {
            await U.ShowError("Playfab api address cannot be an IP");
            return;
        }

        if (viewModel.ChangeXboxABAddress && IPAddress.TryParse(viewModel.XboxABHostname.Split(':')[0], out _))
        {
            await U.ShowError("XboxAB address cannot be an IP");
            return;
        }

        if (viewModel.ChangeXboxLiveAddress && IPAddress.TryParse(viewModel.XboxLiveHostname.Split(':')[0], out _))
        {
            await U.ShowError("Xbox live address cannot be an IP");
            return;
        }

        if (viewModel.ChangeIcon && string.IsNullOrEmpty(viewModel.IconFilePath))
        {
            await U.ShowError("Select an icon file first (or turn 'Change app icon' off)");
            return;
        }

        if (string.IsNullOrEmpty(resourcePackFile))
        {
            var dialog = MessageBoxManager.GetMessageBoxStandard(
                   title: "Info",
                   text: "Resource Pack file not selected. Do you want to continue?",
                   @enum: MsBox.Avalonia.Enums.ButtonEnum.YesNo,
                   icon: MsBox.Avalonia.Enums.Icon.Info,
                   windowStartupLocation: WindowStartupLocation.CenterOwner);

            var result = await dialog.ShowWindowDialogAsync(MainWindow.Instance);

            if (result is MsBox.Avalonia.Enums.ButtonResult.No)
            {
                return;
            }
        }

        MainWindow.Instance.Patch(new Core.ApkProcessor.Options()
        {
            NonInteractive = true,
            InApk = apkFile,
            OutApk = "Solace.apk",
            ResourcePack = resourcePackFile,
            IconPath = viewModel.ChangeIcon ? viewModel.IconFilePath : null,
            DecodedDir = Path.Combine("tmp", "apk"),
            AndroidOSVersion = viewModel.AndroidVersion.Value,
            Patches = viewModel.GetPatches(),
            Variables = viewModel.GetVariables(),
        });
    }

    public async void PickApkFile(object sender, RoutedEventArgs args)
    {
        var topLevel = TopLevel.GetTopLevel(this);

        var files = await topLevel!.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Minecraft earth APK file",
            AllowMultiple = false,
            FileTypeFilter = [ new FilePickerFileType("Apk")
            {
                Patterns = ["*.apk"],
                MimeTypes = ["application/vnd.android.package-archive"]
            } ],
        });

        IStorageFile? file;
        string? filePath = files is not null && (file = files.FirstOrDefault()) is not null
            ? file.Path.LocalPath
            : null;

        if (DataContext is MainViewModel viewModel)
        {
            viewModel.ApkFile = filePath;
        }
    }

    public async void PickResourcePackFile(object sender, RoutedEventArgs args)
    {
        var topLevel = TopLevel.GetTopLevel(this);

        var files = await topLevel!.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Minecraft earth resource pack file",
            AllowMultiple = false,
            FileTypeFilter = [ new FilePickerFileType("Zip")
            {
                Patterns = ["*.zip"],
                MimeTypes = ["application/zip"]
            } ],
        });

        IStorageFile? file;
        string? filePath = files is not null && (file = files.FirstOrDefault()) is not null
            ? file.Path.LocalPath
            : null;

        if (DataContext is MainViewModel viewModel)
        {
            viewModel.ResourcePackFile = filePath;
        }
    }

    public async void PickIconFile(object sender, RoutedEventArgs args)
    {
        var topLevel = TopLevel.GetTopLevel(this);

        var files = await topLevel!.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "App icon image",
            AllowMultiple = false,
            FileTypeFilter = [ new FilePickerFileType("Png")
            {
                Patterns = ["*.png"],
                MimeTypes = ["image/png"]
            } ],
        });

        IStorageFile? file;
        string? filePath = files is not null && (file = files.FirstOrDefault()) is not null
            ? file.Path.LocalPath
            : null;

        if (DataContext is MainViewModel viewModel)
        {
            viewModel.IconFile = filePath;
        }
    }

    public async void PickIpaFile(object sender, RoutedEventArgs args)
    {
        var topLevel = TopLevel.GetTopLevel(this);

        var files = await topLevel!.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Minecraft Earth IPA file",
            AllowMultiple = false,
            FileTypeFilter = [ new FilePickerFileType("Ipa")
            {
                Patterns = ["*.ipa"],
            } ],
        });

        IStorageFile? file;
        string? filePath = files is not null && (file = files.FirstOrDefault()) is not null
            ? file.Path.LocalPath
            : null;

        if (DataContext is MainViewModel viewModel)
        {
            viewModel.IpaFile = filePath;
        }
    }

    public void SelectAndroid(object sender, RoutedEventArgs args)
    {
        if (DataContext is MainViewModel viewModel)
            viewModel.SelectedTabIndex = 0;
    }

    public void SelectIos(object sender, RoutedEventArgs args)
    {
        if (DataContext is MainViewModel viewModel)
            viewModel.SelectedTabIndex = 1;
    }
}
