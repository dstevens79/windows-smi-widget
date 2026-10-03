using System.Globalization;

namespace SMI_Widget;

internal static class Formatting
{
    public static bool IsBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ||
        value.Equals("N/A", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("[N/A]", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("Not Supported", StringComparison.OrdinalIgnoreCase);

    public static string Percent(double? value) => value == null ? "—" : $"{Math.Round(value.Value):0}%";

    public static string Watts(double? value)
    {
        if (value == null) return "—";
        return value.Value >= 100
            ? $"{value.Value.ToString("0", CultureInfo.InvariantCulture)} W"
            : $"{value.Value.ToString("0.0", CultureInfo.InvariantCulture)} W";
    }

    public static string Temp(double? value) =>
        value == null ? "—" : $"{value.Value.ToString("0", CultureInfo.InvariantCulture)}°";

    public static string Mhz(double? value) =>
        value == null ? "—" : value.Value.ToString("0", CultureInfo.InvariantCulture);

    public static string Gib(double? mib)
    {
        if (mib == null) return "—";
        var gb = mib.Value / 1024.0;
        var format = gb >= 10 ? "0.0" : "0.00";
        return gb.ToString(format, CultureInfo.InvariantCulture);
    }

    public static string Humanize(string raw)
    {
        var text = raw.Replace('_', ' ').Trim();
        if (text.Length == 0) return text;
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    public static string SummarizeOutput(string text, string fallback)
    {
        var lines = text.Replace("\r", "")
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .Take(4)
            .ToList();
        if (lines.Count == 0) return fallback;
        var joined = string.Join(" ", lines);
        return joined.Length > 320 ? joined[..320] + "…" : joined;
    }
}
