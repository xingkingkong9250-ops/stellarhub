using Microsoft.Data.Sqlite;

namespace QL.World.Helpers;

/// <summary>Parameter binding helpers enforcing parameterized SQL everywhere.</summary>
public static class SqliteExtensions
{
    public static void AddNullable(this SqliteParameterCollection parameters, string name, object? value)
    {
        parameters.AddWithValue(name, value ?? DBNull.Value);
    }
}
