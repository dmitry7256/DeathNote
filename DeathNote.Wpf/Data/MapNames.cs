namespace DeathNote.Wpf.Data;

/// <summary>
/// Compatible with the Python 2.09 SaveMetadata.map_name rules:
/// strip a scenario path/extension and unify known Russian and English map labels.
/// </summary>
internal static class MapNames
{
    public static string Format(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Unknown";
        string filename = raw.Trim().Replace('\\', '/').Split('/').Last();
        int dot = filename.LastIndexOf('.');
        if (dot > 0) filename = filename[..dot];
        string caption = string.Join(" ", filename.Replace('_', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return caption.ToLowerInvariant() switch
        {
            "west vs east" or "запад против востока" => "West vs East",
            "pangaea" or "пангея" => "Pangaea",
            "continents" or "континенты" => "Continents",
            "archipelago" or "архипелаг" => "Archipelago",
            "fractal" or "фрактал" => "Fractal",
            "earth" or "земля" => "Earth",
            "terra" or "терра" => "Terra",
            "tectonics" or "тектоника" => "Tectonics",
            "continents plus" or "континенты плюс" => "Continents Plus",
            "small continents" or "малые континенты" => "Small Continents",
            "great plains" or "великие равнины" => "Great Plains",
            "inland sea" or "внутреннее море" => "Inland Sea",
            "lakes" or "озёра" or "озера" => "Lakes",
            "oval" or "овал" => "Oval",
            "shuffle" or "случайная" => "Shuffle",
            "" => "Unknown",
            _ => caption
        };
    }
}
