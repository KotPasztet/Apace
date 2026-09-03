namespace Solace.LauncherUI.Models.Db;

/// <summary>
/// Links an admin panel account (<see cref="PanelUserId"/>) to an in-game player profile
/// (<see cref="PlayerId"/> - the live.db Accounts.Id / earth.db player id).
/// </summary>
public class DbLinkedGameAccount
{
    public int Id { get; set; }

    public required string PanelUserId { get; set; }

    public required string PlayerId { get; set; }
}
