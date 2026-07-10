using System.Globalization;

namespace DesktopOrganizer;

internal static class ByteFormatter
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB", "EB"];

    internal static string Format(long bytes) => Format(bytes, CultureInfo.CurrentCulture);

    internal static string Format(long bytes, IFormatProvider formatProvider)
    {
        if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes), bytes, "Byte count cannot be negative.");

        decimal value = bytes;
        int unit = 0;
        while (value >= 1000m && unit < Units.Length - 1)
        {
            value /= 1000m;
            unit++;
        }

        decimal rounded = decimal.Round(value, 2, MidpointRounding.AwayFromZero);
        if (rounded >= 1000m && unit < Units.Length - 1)
        {
            rounded /= 1000m;
            unit++;
        }

        return $"{rounded.ToString("0.##", formatProvider)} {Units[unit]}";
    }

    internal static bool Validate()
    {
        IFormatProvider invariant = CultureInfo.InvariantCulture;
        if (Format(0, invariant) != "0 B" ||
            Format(999, invariant) != "999 B" ||
            Format(1000, invariant) != "1 KB" ||
            Format(1500, invariant) != "1.5 KB" ||
            Format(999_999, invariant) != "1 MB" ||
            Format(1_000_000, invariant) != "1 MB" ||
            Format(1_000_000_000_000_000_000, invariant) != "1 EB")
        {
            return false;
        }

        try
        {
            Format(-1, invariant);
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            return true;
        }
    }
}
