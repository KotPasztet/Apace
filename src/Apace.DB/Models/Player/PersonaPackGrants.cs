namespace Apace.DB.Models.Player;

/// <summary>
/// Per-player skin pack grants (earth.db object type "personaPacks"): persona
/// piece categories the player gets in the live marketplace beyond the panel's
/// default set. Edited from the panel's Skin Packs page; the ApiServer caches
/// reads briefly, so edits apply within ~30 seconds.
/// </summary>
public sealed class PersonaPackGrants
{
    public List<string> Packs { get; set; } = [];
}
