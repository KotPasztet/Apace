namespace Solace.LauncherUI.Patcher;

/// <summary>
/// Everything needed to run one patch job. The page builds this from the
/// selected mode (Auto / Simple / Advanced); the service maps it onto
/// <c>MCEPatcher.Core</c>'s <c>ApkProcessor.Options</c> / <c>IpaProcessor.Options</c>.
/// </summary>
public sealed record PatchRequest
{
    public required PatchMode Mode { get; init; }

    public required PatchPlatform Platform { get; init; }

    /// <summary>Absolute path of the uploaded, original APK/IPA.</summary>
    public required string InputPath { get; init; }

    public required string InputFileName { get; init; }

    /// <summary>Base name of the output file, e.g. "Apace" -> "Apace.apk".</summary>
    public required string OutputBaseName { get; init; }

    /// <summary>Optional resource pack for Android patching (absolute path).</summary>
    public string? ResourcePackPath { get; init; }

    public AndroidRequest? Android { get; init; }

    public IosRequest? Ios { get; init; }
}

public sealed record AndroidRequest
{
    public required int AndroidVersion { get; init; }

    public required bool ChangeLocatorAddress { get; init; }
    public required PatchProtocol LocatorProtocol { get; init; }
    public required string LocatorHostname { get; init; }

    public required bool DisableSunsetTimeCheck { get; init; }
    public required bool DisableLicenseCheck { get; init; }
    public required bool DisableTelemetry { get; init; }
    public required bool DisableMsaLoginSignatureValidation { get; init; }

    public required bool ChangeAppName { get; init; }
    public required string AppName { get; init; }
    public required string AppNameShort { get; init; }

    public required bool ChangePackageName { get; init; }
    public required string PackageName { get; init; }

    /// <summary>True: the Apace icon replaces the launcher icon. Null icon path -> panel default.</summary>
    public required bool ChangeIcon { get; init; }

    public required bool LoginServerSingleDomainMode { get; init; }
    public required PatchProtocol LoginServerProtocol { get; init; }
    public required string LoginServerHostname { get; init; }

    public required bool ChangeMsaLoginServiceAddress { get; init; }
    public required PatchProtocol MsaLoginServiceProtocol { get; init; }
    public required string MsaLoginServiceHostname { get; init; }

    public required bool ChangePlayfabApiAddress { get; init; }
    public required PatchProtocol PlayfabApiProtocol { get; init; }
    public required string PlayfabApiHostname { get; init; }

    public required bool ChangeXboxAbAddress { get; init; }
    public required PatchProtocol XboxAbProtocol { get; init; }
    public required string XboxAbHostname { get; init; }

    public required bool ChangeXboxLiveAddress { get; init; }
    public required PatchProtocol XboxLiveProtocol { get; init; }
    public required string XboxLiveHostname { get; init; }
}

public sealed record IosRequest
{
    public required bool ChangeLocatorAddress { get; init; }
    public required PatchProtocol LocatorProtocol { get; init; }
    public required string LocatorHostname { get; init; }

    public required bool DisableSunsetTimeCheck { get; init; }
    public required bool DisableSpeedLimit { get; init; }

    public required bool ChangeAppName { get; init; }
    public required string AppName { get; init; }

    /// <summary>True: the Apace icon replaces the launcher icon. Null icon path -> panel default.</summary>
    public required bool ChangeIcon { get; init; }

    public required bool RemoveDrm { get; init; }

    /// <summary>Master toggle for redirecting the login services (XAL/PlayFab/XboxAB/XboxLive).</summary>
    public required bool ChangeLoginServerAddress { get; init; }

    public required bool ChangeXalAuthAddress { get; init; }
    public required bool ChangePlayfabApiAddress { get; init; }
    public required bool ChangeXboxAbAddress { get; init; }
    public required bool ChangeXboxLiveAddress { get; init; }

    public required bool ForceInAppWebView { get; init; }
    public required bool ForceInteractiveSignIn { get; init; }

    /// <summary>
    /// True: all login services share one address (Protocol/Hostname).
    /// False: per-service addresses (multi-domain mode).
    /// </summary>
    public required bool LoginServerSingleDomainMode { get; init; }

    // single-domain address (also the fallback in multi-domain mode)
    public required PatchProtocol LoginServerProtocol { get; init; }
    public required string LoginServerHostname { get; init; }

    public required PatchProtocol XalProtocol { get; init; }
    public required string XalHostname { get; init; }

    public required PatchProtocol PlayfabApiProtocol { get; init; }
    public required string PlayfabApiHostname { get; init; }

    public required PatchProtocol XboxAbProtocol { get; init; }
    public required string XboxAbHostname { get; init; }

    public required PatchProtocol XboxLiveProtocol { get; init; }
    public required string XboxLiveHostname { get; init; }
}
