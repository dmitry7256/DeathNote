// Generated from Death Note 2.09 SaveMetadata.CIV_ALIASES for compatibility.
namespace DeathNote.Wpf.Data;
internal static class CivilizationNames
{
    private static readonly Dictionary<string,string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["america"]="America",
        ["arabia"]="Arabia",
        ["assyria"]="Assyria",
        ["austria"]="Austria",
        ["aztec"]="Aztecs",
        ["aztecs"]="Aztecs",
        ["babylon"]="Babylon",
        ["brazil"]="Brazil",
        ["byzantium"]="Byzantium",
        ["carthage"]="Carthage",
        ["celts"]="Celts",
        ["china"]="China",
        ["denmark"]="Denmark",
        ["egypt"]="Egypt",
        ["england"]="England",
        ["ethiopia"]="Ethiopia",
        ["france"]="France",
        ["germany"]="Germany",
        ["greece"]="Greece",
        ["huns"]="Huns",
        ["inca"]="Inca",
        ["india"]="India",
        ["indonesia"]="Indonesia",
        ["iroquois"]="Iroquois",
        ["japan"]="Japan",
        ["korea"]="Korea",
        ["maya"]="Maya",
        ["mongolia"]="Mongolia",
        ["morocco"]="Morocco",
        ["netherlands"]="Netherlands",
        ["ottoman"]="Ottomans",
        ["ottomans"]="Ottomans",
        ["persia"]="Persia",
        ["poland"]="Poland",
        ["polynesia"]="Polynesia",
        ["portugal"]="Portugal",
        ["rome"]="Rome",
        ["russia"]="Russia",
        ["shoshone"]="Shoshone",
        ["siam"]="Siam",
        ["songhai"]="Songhai",
        ["spain"]="Spain",
        ["sweden"]="Sweden",
        ["venice"]="Venice",
        ["zulu"]="Zulu",
        ["австрия"]="Austria",
        ["америка"]="America",
        ["англия"]="England",
        ["аравия"]="Arabia",
        ["ассирия"]="Assyria",
        ["ацтеки"]="Aztecs",
        ["бразилия"]="Brazil",
        ["вавилон"]="Babylon",
        ["венеция"]="Venice",
        ["византийская империя"]="Byzantium",
        ["византия"]="Byzantium",
        ["германия"]="Germany",
        ["голландия"]="Netherlands",
        ["греция"]="Greece",
        ["гунны"]="Huns",
        ["дания"]="Denmark",
        ["египет"]="Egypt",
        ["зулу"]="Zulu",
        ["зулусы"]="Zulu",
        ["индия"]="India",
        ["индонезия"]="Indonesia",
        ["инки"]="Inca",
        ["ирокезы"]="Iroquois",
        ["испания"]="Spain",
        ["карфаген"]="Carthage",
        ["кельты"]="Celts",
        ["китай"]="China",
        ["корея"]="Korea",
        ["майя"]="Maya",
        ["марокко"]="Morocco",
        ["монголия"]="Mongolia",
        ["нидерланды"]="Netherlands",
        ["османская империя"]="Ottomans",
        ["турция"]="Turkey",
        ["turkey"]="Turkey",
        ["османы"]="Ottomans",
        ["персия"]="Persia",
        ["полинезия"]="Polynesia",
        ["польша"]="Poland",
        ["португалия"]="Portugal",
        ["рим"]="Rome",
        ["римская империя"]="Rome",
        ["россия"]="Russia",
        ["сиам"]="Siam",
        ["сонгай"]="Songhai",
        ["сша"]="America",
        ["франция"]="France",
        ["швеция"]="Sweden",
        ["шошоны"]="Shoshone",
        // Common localized, plural and descriptive names found in older saves and mods.
        ["the aztecs"]="Aztecs",
        ["aztec empire"]="Aztecs",
        ["the celts"]="Celts",
        ["the huns"]="Huns",
        ["mayan"]="Maya",
        ["mayans"]="Maya",
        ["dutch"]="Netherlands",
        ["ottoman empire"]="Ottomans",
        ["the ottomans"]="Ottomans",
        ["roman empire"]="Rome",
        ["the shoshone"]="Shoshone",
        ["американцы"]="America",
        ["арабы"]="Arabia",
        ["ацтекская империя"]="Aztecs",
        ["византийцы"]="Byzantium",
        ["голландцы"]="Netherlands",
        ["майянцы"]="Maya",
        ["монголы"]="Mongolia",
        ["османская держава"]="Ottomans",
        ["оттоманская империя"]="Ottomans",
        ["римляне"]="Rome",
        ["турки"]="Turkey",
        ["турецкая империя"]="Turkey",
        ["эфиопия"]="Ethiopia",
        ["япония"]="Japan",
    };
    // The game and older save versions produce CIVILIZATION_*, TXT_KEY_CIV_*
    // and translated names. Normalize all of them before resolving an emblem.
    public static string Format(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return name;
        var key = name.Trim();
        bool keyFromGame = false;
        const StringComparison cmp = StringComparison.OrdinalIgnoreCase;
        if (key.StartsWith("TXT_KEY_CIVILIZATION_", cmp))
        {
            key = key["TXT_KEY_CIVILIZATION_".Length..];
            keyFromGame = true;
        }
        else if (key.StartsWith("TXT_KEY_CIV_", cmp))
        {
            key = key["TXT_KEY_CIV_".Length..];
            keyFromGame = true;
        }
        else if (key.StartsWith("CIVILIZATION_", cmp))
        {
            key = key["CIVILIZATION_".Length..];
            keyFromGame = true;
        }
        if (keyFromGame)
        {
            foreach (var suffix in new[] { "_SHORT_DESC", "_DESCRIPTION", "_DESC", "_NAME" })
            {
                if (!key.EndsWith(suffix, cmp)) continue;
                key = key[..^suffix.Length];
                break;
            }
        }
        key = key.Replace('_', ' ').Trim();
        if (Names.TryGetValue(key, out var found)) return found;
        return keyFromGame
            ? System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(key.ToLowerInvariant())
            : key;
    }
}
