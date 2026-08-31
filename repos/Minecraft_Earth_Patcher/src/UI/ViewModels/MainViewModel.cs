using MCEPatcher.UI.Models;
using MCEPatcher.UI.Utils;
using ReactiveUI;
using System.Collections.Generic;
using System.Reflection;

namespace MCEPatcher.UI.ViewModels;

/*
 * color definitions from: https://github.com/AvaloniaUI/Avalonia/blob/master/src/Avalonia.Themes.Fluent/Accents/FluentControlResources.xaml
 */

public sealed class MainViewModel : ViewModelBase
{
    public string AssemblyVersion => Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";

    private bool _isSimpleMode;
    public bool IsSimpleMode
    {
        get => _isSimpleMode;
        set
        {
            this.RaiseAndSetIfChanged(ref _isSimpleMode, value);
            if (value)
            {
                EnforceSimpleModeDefaults();
            }
        }
    }

    private string? apkFile;
    public string? ApkFile
    {
        get => "APK File: " + (U.LimitLengthMiddle(apkFile, 60) ?? "Not selected");
        set => this.RaiseAndSetIfChanged(ref apkFile, value);
    }
    public string? ApkFilePath
    {
        get => apkFile;
        set => this.RaiseAndSetIfChanged(ref apkFile, value);
    }

    private string? resourcePackFile;
    public string? ResourcePackFile
    {
        get => "Resource Pack File (Optional): " + (U.LimitLengthMiddle(resourcePackFile, 60) ?? "Not selected");
        set => this.RaiseAndSetIfChanged(ref resourcePackFile, value);
    }
    public string? ResourcePackFilePath
    {
        get => resourcePackFile;
        set => this.RaiseAndSetIfChanged(ref resourcePackFile, value);
    }

    private bool changeLocatorAddress;
    public bool ChangeLocatorAddress
    {
        get => changeLocatorAddress;
        set => this.RaiseAndSetIfChanged(ref changeLocatorAddress, value);
    }
    private int locatorProtocol;
    public int LocatorProtocol
    {
        get => locatorProtocol;
        set => this.RaiseAndSetIfChanged(ref locatorProtocol, value);
    }
    private string locatorHostname;
    public string LocatorHostname
    {
        get => locatorHostname;
        set => this.RaiseAndSetIfChanged(ref locatorHostname, value);
    }
    private bool disableSunsetTimeCheck;
    public bool DisableSunsetTimeCheck
    {
        get => disableSunsetTimeCheck;
        set => this.RaiseAndSetIfChanged(ref disableSunsetTimeCheck, value);
    }
    private bool disableLicenseCheck;
    public bool DisableLicenseCheck
    {
        get => disableLicenseCheck;
        set => this.RaiseAndSetIfChanged(ref disableLicenseCheck, value);
    }
    private bool disableTelemetry;
    public bool DisableTelemetry
    {
        get => disableTelemetry;
        set => this.RaiseAndSetIfChanged(ref disableTelemetry, value);
    }
    private bool disableMsaLoginSignatureValidation;
    public bool DisableMsaLoginSignatureValidation
    {
        get => disableMsaLoginSignatureValidation;
        set
        {
            this.RaiseAndSetIfChanged(ref disableMsaLoginSignatureValidation, value);
            if (!value) ChangeMSALoginServiceAddress = false;
        }
    }
    private bool changeAppName;
    public bool ChangeAppName
    {
        get => changeAppName;
        set => this.RaiseAndSetIfChanged(ref changeAppName, value);
    }
    private string appName;
    public string AppName
    {
        get => appName;
        set => this.RaiseAndSetIfChanged(ref appName, value);
    }
    private string appNameShort;
    public string AppNameShort
    {
        get => appNameShort;
        set => this.RaiseAndSetIfChanged(ref appNameShort, value);
    }
    private bool changePackageName;
    public bool ChangePackageName
    {
        get => changePackageName;
        set => this.RaiseAndSetIfChanged(ref changePackageName, value);
    }
    private string packageName;
    public string PackageName
    {
        get => packageName;
        set => this.RaiseAndSetIfChanged(ref packageName, value);
    }

    private bool changeIcon;
    public bool ChangeIcon
    {
        get => changeIcon;
        set => this.RaiseAndSetIfChanged(ref changeIcon, value);
    }

    private string? iconFile;
    public string? IconFile
    {
        get => "Icon File: " + (U.LimitLengthMiddle(iconFile, 60) ?? "Not selected");
        set => this.RaiseAndSetIfChanged(ref iconFile, value);
    }
    public string? IconFilePath
    {
        get => iconFile;
        set => this.RaiseAndSetIfChanged(ref iconFile, value);
    }
    private int? androidVersion;
    public int? AndroidVersion
    {
        get => androidVersion;
        set => this.RaiseAndSetIfChanged(ref androidVersion, value);
    }
    private bool loginServerSingleDomainMode;
    public bool LoginServerSingleDomainMode
    {
        get => loginServerSingleDomainMode;
        set => this.RaiseAndSetIfChanged(ref loginServerSingleDomainMode, value);
    }
    private int loginServerProtocol;
    public int LoginServerProtocol
    {
        get => loginServerProtocol;
        set => this.RaiseAndSetIfChanged(ref loginServerProtocol, value);
    }
    private string loginServerHostname;
    public string LoginServerHostname
    {
        get => loginServerHostname;
        set => this.RaiseAndSetIfChanged(ref loginServerHostname, value);
    }
    private bool changeMSALoginServiceAddress;
    public bool ChangeMSALoginServiceAddress
    {
        get => changeMSALoginServiceAddress;
        set
        {
            this.RaiseAndSetIfChanged(ref changeMSALoginServiceAddress, value);
            if (value) DisableMsaLoginSignatureValidation = true;
        }
    }
    private int _MSALoginServiceProtocol;
    public int MSALoginServiceProtocol
    {
        get => _MSALoginServiceProtocol;
        set => this.RaiseAndSetIfChanged(ref _MSALoginServiceProtocol, value);
    }
    private string _MSALoginServiceHostname;
    public string MSALoginServiceHostname
    {
        get => _MSALoginServiceHostname;
        set => this.RaiseAndSetIfChanged(ref _MSALoginServiceHostname, value);
    }
    private bool changePlayfabApiAddress;
    public bool ChangePlayfabApiAddress
    {
        get => changePlayfabApiAddress;
        set => this.RaiseAndSetIfChanged(ref changePlayfabApiAddress, value);
    }
    private int playfabApiProtocol;
    public int PlayfabApiProtocol
    {
        get => playfabApiProtocol;
        set => this.RaiseAndSetIfChanged(ref playfabApiProtocol, value);
    }
    private string playfabApiHostname;
    public string PlayfabApiHostname
    {
        get => playfabApiHostname;
        set => this.RaiseAndSetIfChanged(ref playfabApiHostname, value);
    }
    private bool changeXboxABAddress;
    public bool ChangeXboxABAddress
    {
        get => changeXboxABAddress;
        set => this.RaiseAndSetIfChanged(ref changeXboxABAddress, value);
    }
    private int xboxABProtocol;
    public int XboxABProtocol
    {
        get => xboxABProtocol;
        set => this.RaiseAndSetIfChanged(ref xboxABProtocol, value);
    }
    private string xboxABHostname;
    public string XboxABHostname
    {
        get => xboxABHostname;
        set => this.RaiseAndSetIfChanged(ref xboxABHostname, value);
    }
    private bool changeXboxLiveAddress;
    public bool ChangeXboxLiveAddress
    {
        get => changeXboxLiveAddress;
        set => this.RaiseAndSetIfChanged(ref changeXboxLiveAddress, value);
    }
    private int xboxLiveProtocol;
    public int XboxLiveProtocol
    {
        get => xboxLiveProtocol;
        set => this.RaiseAndSetIfChanged(ref xboxLiveProtocol, value);
    }
    private string xboxLiveHostname;
    public string XboxLiveHostname
    {
        get => xboxLiveHostname;
        set => this.RaiseAndSetIfChanged(ref xboxLiveHostname, value);
    }

    private int _selectedTabIndex;
    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedTabIndex, value);
            this.RaisePropertyChanged(nameof(IsAndroidSelected));
            this.RaisePropertyChanged(nameof(IsIosSelected));
        }
    }
    public bool IsAndroidSelected => _selectedTabIndex == 0;
    public bool IsIosSelected => _selectedTabIndex == 1;

    private string? ipaFile;
    public string? IpaFile
    {
        get => "IPA File: " + (U.LimitLengthMiddle(ipaFile, 60) ?? "Not selected");
        set => this.RaiseAndSetIfChanged(ref ipaFile, value);
    }
    public string? IpaFilePath
    {
        get => ipaFile;
        set => this.RaiseAndSetIfChanged(ref ipaFile, value);
    }

    private bool _iosIsSimpleMode;
    public bool IosIsSimpleMode
    {
        get => _iosIsSimpleMode;
        set
        {
            this.RaiseAndSetIfChanged(ref _iosIsSimpleMode, value);
            if (value)
            {
                EnforceIosSimpleModeDefaults();
            }
        }
    }

    private bool iosChangeLocatorAddress;
    public bool IosChangeLocatorAddress
    {
        get => iosChangeLocatorAddress;
        set => this.RaiseAndSetIfChanged(ref iosChangeLocatorAddress, value);
    }
    private bool iosDisableSunsetTimeCheck;
    public bool IosDisableSunsetTimeCheck
    {
        get => iosDisableSunsetTimeCheck;
        set => this.RaiseAndSetIfChanged(ref iosDisableSunsetTimeCheck, value);
    }
    private bool iosDisableSpeedLimit;
    public bool IosDisableSpeedLimit
    {
        get => iosDisableSpeedLimit;
        set => this.RaiseAndSetIfChanged(ref iosDisableSpeedLimit, value);
    }
    private bool iosChangeAppName;
    public bool IosChangeAppName
    {
        get => iosChangeAppName;
        set => this.RaiseAndSetIfChanged(ref iosChangeAppName, value);
    }
    private bool iosRemoveDrm;
    public bool IosRemoveDrm
    {
        get => iosRemoveDrm;
        set => this.RaiseAndSetIfChanged(ref iosRemoveDrm, value);
    }

    private bool iosChangeXalAuthAddress;
    public bool IosChangeXalAuthAddress
    {
        get => iosChangeXalAuthAddress;
        set => this.RaiseAndSetIfChanged(ref iosChangeXalAuthAddress, value);
    }

    private bool iosForceInAppWebView = true;
    public bool IosForceInAppWebView
    {
        get => iosForceInAppWebView;
        set => this.RaiseAndSetIfChanged(ref iosForceInAppWebView, value);
    }

    private bool iosForceInteractiveSignIn = true;
    public bool IosForceInteractiveSignIn
    {
        get => iosForceInteractiveSignIn;
        set => this.RaiseAndSetIfChanged(ref iosForceInteractiveSignIn, value);
    }

    private bool iosUseCustomAuthServer;
    public bool IosUseCustomAuthServer
    {
        get => iosUseCustomAuthServer;
        set => this.RaiseAndSetIfChanged(ref iosUseCustomAuthServer, value);
    }

    private int _iosProtocol = (int)ProtocolEnum.Http;
    public int IosProtocol
    {
        get => _iosProtocol;
        set => this.RaiseAndSetIfChanged(ref _iosProtocol, value);
    }

    private string _iosHostname = "";
    public string IosHostname
    {
        get => _iosHostname;
        set => this.RaiseAndSetIfChanged(ref _iosHostname, value);
    }

    private int _iosLocatorProtocol = (int)ProtocolEnum.Http;
    public int IosLocatorProtocol
    {
        get => _iosLocatorProtocol;
        set => this.RaiseAndSetIfChanged(ref _iosLocatorProtocol, value);
    }

    private string _iosLocatorHostname = "";
    public string IosLocatorHostname
    {
        get => _iosLocatorHostname;
        set => this.RaiseAndSetIfChanged(ref _iosLocatorHostname, value);
    }

    private string _iosAppName = "Solace";
    public string IosAppName
    {
        get => _iosAppName;
        set => this.RaiseAndSetIfChanged(ref _iosAppName, value);
    }

    private bool _iosChangeIcon;
    public bool IosChangeIcon
    {
        get => _iosChangeIcon;
        set => this.RaiseAndSetIfChanged(ref _iosChangeIcon, value);
    }

    private bool iosChangePlayfabApiAddress;
    public bool IosChangePlayfabApiAddress
    {
        get => iosChangePlayfabApiAddress;
        set => this.RaiseAndSetIfChanged(ref iosChangePlayfabApiAddress, value);
    }

    private bool iosChangeXboxABAddress;
    public bool IosChangeXboxABAddress
    {
        get => iosChangeXboxABAddress;
        set => this.RaiseAndSetIfChanged(ref iosChangeXboxABAddress, value);
    }

    private bool iosChangeXboxLiveAddress;
    public bool IosChangeXboxLiveAddress
    {
        get => iosChangeXboxLiveAddress;
        set => this.RaiseAndSetIfChanged(ref iosChangeXboxLiveAddress, value);
    }

    private bool iosLoginServerSingleDomainMode;
    public bool IosLoginServerSingleDomainMode
    {
        get => iosLoginServerSingleDomainMode;
        set => this.RaiseAndSetIfChanged(ref iosLoginServerSingleDomainMode, value);
    }

    private int _iosXalProtocol = (int)ProtocolEnum.Http;
    public int IosXalProtocol
    {
        get => _iosXalProtocol;
        set => this.RaiseAndSetIfChanged(ref _iosXalProtocol, value);
    }

    private string _iosXalHostname = "";
    public string IosXalHostname
    {
        get => _iosXalHostname;
        set => this.RaiseAndSetIfChanged(ref _iosXalHostname, value);
    }

    private int _iosPlayfabApiProtocol = (int)ProtocolEnum.Http;
    public int IosPlayfabApiProtocol
    {
        get => _iosPlayfabApiProtocol;
        set => this.RaiseAndSetIfChanged(ref _iosPlayfabApiProtocol, value);
    }

    private string _iosPlayfabApiHostname = "";
    public string IosPlayfabApiHostname
    {
        get => _iosPlayfabApiHostname;
        set => this.RaiseAndSetIfChanged(ref _iosPlayfabApiHostname, value);
    }

    private int _iosXboxABProtocol = (int)ProtocolEnum.Http;
    public int IosXboxABProtocol
    {
        get => _iosXboxABProtocol;
        set => this.RaiseAndSetIfChanged(ref _iosXboxABProtocol, value);
    }

    private string _iosXboxABHostname = "";
    public string IosXboxABHostname
    {
        get => _iosXboxABHostname;
        set => this.RaiseAndSetIfChanged(ref _iosXboxABHostname, value);
    }

    private int _iosXboxLiveProtocol = (int)ProtocolEnum.Http;
    public int IosXboxLiveProtocol
    {
        get => _iosXboxLiveProtocol;
        set => this.RaiseAndSetIfChanged(ref _iosXboxLiveProtocol, value);
    }

    private string _iosXboxLiveHostname = "";
    public string IosXboxLiveHostname
    {
        get => _iosXboxLiveHostname;
        set => this.RaiseAndSetIfChanged(ref _iosXboxLiveHostname, value);
    }

    private void EnforceIosSimpleModeDefaults()
    {
        IosChangeLocatorAddress = true;
        IosDisableSunsetTimeCheck = true;
        IosDisableSpeedLimit = false;
        IosChangeAppName = true;
        IosRemoveDrm = true;
        IosChangeXalAuthAddress = true;
        IosForceInAppWebView = true;
        IosForceInteractiveSignIn = true;
        IosUseCustomAuthServer = false;
        IosChangePlayfabApiAddress = true;
        IosChangeXboxABAddress = true;
        IosChangeXboxLiveAddress = true;
    }

    public MainViewModel()
    {
        ChangeLocatorAddress = true;
        LocatorProtocol = (int)ProtocolEnum.Http;
        LocatorHostname = "";
        DisableSunsetTimeCheck = true;
        DisableLicenseCheck = true;
        DisableTelemetry = true;
        DisableMsaLoginSignatureValidation = true;
        ChangeAppName = true;
        AppName = "Solace";
        AppNameShort = "Solace";
        ChangePackageName = true;
        PackageName = "com.github.earth_restored";
        LoginServerSingleDomainMode = true;
        LoginServerProtocol = (int)ProtocolEnum.Http;
        LoginServerHostname = "";
        ChangeMSALoginServiceAddress = false;
        MSALoginServiceProtocol = (int)ProtocolEnum.Https;
        MSALoginServiceHostname = "live.com";
        ChangePlayfabApiAddress = false;
        PlayfabApiProtocol = (int)ProtocolEnum.Https;
        PlayfabApiHostname = "playfabapi.com";
        ChangeXboxABAddress = false;
        XboxABProtocol = (int)ProtocolEnum.Https;
        XboxABHostname = "xboxab.com";
        ChangeXboxLiveAddress = false;
        XboxLiveProtocol = (int)ProtocolEnum.Https;
        XboxLiveHostname = "xboxlive.com";

        IosProtocol = (int)ProtocolEnum.Http;
        IosLocatorProtocol = (int)ProtocolEnum.Http;
        IosLoginServerSingleDomainMode = true;
        IosXalProtocol = (int)ProtocolEnum.Http;
        IosXalHostname = "";
        IosPlayfabApiProtocol = (int)ProtocolEnum.Http;
        IosPlayfabApiHostname = "";
        IosXboxABProtocol = (int)ProtocolEnum.Http;
        IosXboxABHostname = "";
        IosXboxLiveProtocol = (int)ProtocolEnum.Http;
        IosXboxLiveHostname = "";

        IsSimpleMode = true;
        IosIsSimpleMode = true;
    }

    public IEnumerable<string> GetPatches()
    {
        yield return "fix-official-msa-login-after-signature-change";
        if (DisableSunsetTimeCheck) yield return "disable-sunset-time-check";
        if (DisableLicenseCheck) yield return "disable-license-check";
        if (DisableTelemetry) yield return "disable-telemetry";
        if (DisableMsaLoginSignatureValidation) yield return "disable-msa-login-signature-validation";
        if (ChangeLocatorAddress) yield return "change-locator-address";
        if (ChangeAppName) yield return "change-app-name";
        if (ChangePackageName) yield return "change-package-name";
        if (ChangeMSALoginServiceAddress) yield return "change-msa-login-address";
        if (ChangePlayfabApiAddress) yield return "change-playfab-address";
        if (ChangeXboxABAddress) yield return "change-xboxab-address";
        if (ChangeXboxLiveAddress)
        {
            yield return "change-xboxlive-address-base";
            yield return "change-xboxlive-address-extra";
        }
        yield return "add-arcore-apikey"; // make sure it runs after change-package-name
    }

    public IEnumerable<string> GetVariables()
    {
        if (ChangeLocatorAddress)
        {
            yield return $"locatorprotocol={((ProtocolEnum)LocatorProtocol).ToProtocolString()}://";
            yield return $"locatorhostname={LocatorHostname}";
        }
        if (ChangeAppName)
        {
            yield return $"app_name={AppName}";
            yield return $"app_name_short={AppNameShort}";
        }
        if (ChangePackageName)
        {
            yield return $"package_name={PackageName}";
        }
        if (LoginServerSingleDomainMode)
        {
            if (ChangeMSALoginServiceAddress)
            {
                yield return $"liveprotocol={((ProtocolEnum)LoginServerProtocol).ToProtocolString()}://{LoginServerHostname}/";
                yield return "livehostname=live.com";
            }
            if (ChangePlayfabApiAddress)
            {
                yield return $"playfabprotocol={((ProtocolEnum)LoginServerProtocol).ToProtocolString()}://{LoginServerHostname}/";
                yield return "playfabhostname=playfabapi.com";
            }
            if (ChangeXboxABAddress)
            {
                yield return $"xboxabprotocol={((ProtocolEnum)LoginServerProtocol).ToProtocolString()}://{LoginServerHostname}/";
                yield return "xboxabhostname=xboxab.com";
            }
            if (ChangeXboxLiveAddress)
            {
                yield return $"xboxliveprotocol={((ProtocolEnum)LoginServerProtocol).ToProtocolString()}://{LoginServerHostname}/";
                yield return "xboxlivehostname=xboxlive.com";
            }
        }
        else
        {
            if (ChangeMSALoginServiceAddress)
            {
                yield return $"liveprotocol={((ProtocolEnum)MSALoginServiceProtocol).ToProtocolString()}://";
                yield return $"livehostname={MSALoginServiceHostname}";
            }
            if (ChangePlayfabApiAddress)
            {
                yield return $"playfabprotocol={((ProtocolEnum)PlayfabApiProtocol).ToProtocolString()}://";
                yield return $"playfabhostname={PlayfabApiHostname}";
            }
            if (ChangeXboxABAddress)
            {
                yield return $"xboxabprotocol={((ProtocolEnum)XboxABProtocol).ToProtocolString()}://";
                yield return $"xboxabhostname={XboxABHostname}";
            }
            if (ChangeXboxLiveAddress)
            {
                yield return $"xboxliveprotocol={((ProtocolEnum)XboxLiveProtocol).ToProtocolString()}://";
                yield return $"xboxlivehostname={XboxLiveHostname}";
            }
        }
    }

    private void EnforceSimpleModeDefaults()
    {
        ChangeLocatorAddress = true;
        DisableSunsetTimeCheck = true;
        DisableLicenseCheck = true;
        DisableTelemetry = true;
        DisableMsaLoginSignatureValidation = true;
        ChangeAppName = true;
        ChangePackageName = true;
        LoginServerSingleDomainMode = true;

        ChangeMSALoginServiceAddress = true;
        ChangePlayfabApiAddress = true;
        ChangeXboxABAddress = true;
        ChangeXboxLiveAddress = true;
    }
}