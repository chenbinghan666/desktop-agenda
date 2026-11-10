using System;
using System.IO;
using System.Text.Json;

internal sealed record DisplayOptions(string StartView = "", bool SundayFirst = false, int StartHour = 8,
    int EndHour = 21, int HourHeight = 56, bool ShowNow = true, bool FitDay = true,
    string TimedColor = "#C0D3EF", string AllDayColor = "#BEDCD0", int OpacityPercent = 100, int TextSize = 14,
    bool PositionLocked = false)
{
    private static readonly string Pathname = Path.Combine(Program.DirectoryPath, "display.json");

    internal static DisplayOptions Load()
    {
        try
        {
            if (!File.Exists(Pathname)) return new DisplayOptions();
            var value = JsonSerializer.Deserialize<DisplayOptions>(File.ReadAllText(Pathname));
            if (value is not null && value.StartHour is >= 0 and <= 23 && value.EndHour is >= 1 and <= 24 &&
                value.EndHour > value.StartHour && value.HourHeight is >= 44 and <= 80 &&
                value.OpacityPercent is >= 50 and <= 100 && value.TextSize is >= 11 and <= 20 &&
                ValidColor(value.TimedColor) && ValidColor(value.AllDayColor))
                return value;
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
        return new DisplayOptions();
    }

    private static bool ValidColor(string? value) => value is { Length: 7 } && value[0] == '#' &&
        int.TryParse(value.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out _);

    internal void Save() => Program.AtomicWrite(Pathname, [JsonSerializer.Serialize(this)]);
}
