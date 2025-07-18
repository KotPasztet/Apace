using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using ViennaDotNet.DB;
using ViennaDotNet.Launcher.Utils;
using ViennaDotNet.Launcher.Windows.Utils;

namespace ViennaDotNet.Launcher.Windows.Data;

internal sealed class PlayersWindow : Window
{
    private readonly EarthDB _db;

    public PlayersWindow(EarthDB db, Settings settings)
    {
        _db = db;

        Title = "Manage data/Players";

        var editBtn = new Button()
        {
            Text = "_Edit",
            X = Pos.Center(),
            Y = Pos.Absolute(1),
        };
        editBtn.Accepting += async (s, e) =>
        {
            e.Handled = true;

            if (await DataUtils.GetPlayerCountAsync(_db) is null or 0)
            {
                MessageBox.ErrorQuery("No players", "There are no players in the database.", "OK");
                return;
            }

            using var liveDb = DataUtils.OpenLiveDB(settings);
            string? selected = SelectDialog.Show("Select player to edit", DataUtils.GetFullProfilesAsync(_db, liveDb).Select(item => $"{item.Id}{(item.Username is not null ? $" \"{item.Username}\" " : " ")}{item.Profile.Level}LV {item.Profile.Rubies.Total} Rubies").ToBlockingEnumerable());

            if (selected is null)
            {
                return;
            }

            // TODO: get id and username in a better way
            Application.Run(new PlayerWindow(selected[..selected.IndexOf(' ')], selected.Contains('"') ? selected[(selected.IndexOf('"') + 1)..selected.LastIndexOf('"')] : null, db));
        };

        var removeBtn = new Button()
        {
            Text = "_Remove",
            X = Pos.Center(),
            Y = Pos.Bottom(editBtn) + 1,
        };
        removeBtn.Accepting += (s, e) =>
        {
            e.Handled = true;
        };

        var backBtn = new Button()
        {
            Text = "_Back",
            X = Pos.Center(),
            Y = Pos.Bottom(removeBtn) + 1,
        };
        backBtn.Accepting += (s, e) =>
        {
            e.Handled = true;

            Application.RequestStop(this);
        };

        Add(editBtn,
            removeBtn,
            backBtn);
    }
}