using System.Text.Json;

namespace Apace.DB.Models.Player;

// Player persona (skin) profiles as sent by the client through the PlayFab
// Object API. Object names are "personaProfile".."personaProfile4" and values
// are the raw DataObject JSON blobs. Persisted per player so the selected
// skin, arm size and colors survive logouts and server restarts instead of
// being hardcoded for everyone.
public sealed class PersonaProfiles
{
    public Dictionary<string, JsonElement> Objects { get; set; } = [];
}
