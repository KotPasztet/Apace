using ViennaDotNet.LauncherUI.Components.Pages;

namespace ViennaDotNet.LauncherUI;

public static class ServerManager
{
    public static ServerStatus Status { get; private set; }

    
}

public enum SeverStatus
{
    Online,
    Starting,
    Offline,
}