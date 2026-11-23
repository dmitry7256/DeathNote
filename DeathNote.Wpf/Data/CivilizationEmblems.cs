namespace DeathNote.Wpf.Data;

/// <summary>
/// Resolve the same local Civilization V emblems used by the Python edition.
/// Unknown / empty values deliberately yield no icon instead of an invalid image URI.
/// </summary>
internal static class CivilizationEmblems
{
    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        "AMERICA", "ARABIA", "ASSYRIA", "AUSTRIA", "AZTEC", "BABYLON",
        "BRAZIL", "BYZANTIUM", "CARTHAGE", "CELTS", "CHINA", "DENMARK",
        "EGYPT", "ENGLAND", "ETHIOPIA", "FRANCE", "GERMANY", "GREECE",
        "HUNS", "INCA", "INDIA", "INDONESIA", "IROQUOIS", "JAPAN",
        "KOREA", "MAYA", "MONGOLIA", "MOROCCO", "NETHERLANDS", "OTTOMAN",
        "PERSIA", "POLAND", "POLYNESIA", "PORTUGAL", "ROME", "RUSSIA",
        "SHOSHONE", "SIAM", "SONGHAI", "SPAIN", "SWEDEN", "VENICE", "ZULU"
    };

    public static string? Icon(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        // CivilizationNames handles every known game prefix, both localization
        // key formats, Russian/English names and extra aliases in one place.
        var normalized = CivilizationNames.Format(value).ToUpperInvariant() switch
        {
            "AZTECS" => "AZTEC",
            "OTTOMANS" => "OTTOMAN",
            "TURKEY" => "OTTOMAN",
            var key => key
        };
        return Known.Contains(normalized) ? $"/Assets/civilizations/{normalized}.png" : null;
    }
}
