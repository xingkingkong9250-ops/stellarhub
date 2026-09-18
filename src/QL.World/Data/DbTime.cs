using System.Globalization;

namespace QL.World.Data;

/// <summary>UTC ISO-8601 timestamps used in every table.</summary>
internal static class DbTime
{
    private const string FORMAT = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    public static string Format(DateTime value) =>
        value.ToUniversalTime().ToString(FORMAT, CultureInfo.InvariantCulture);

    public static string Now() => Format(DateTime.UtcNow);

    public static DateTime Parse(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
