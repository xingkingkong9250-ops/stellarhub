using System.Globalization;
using Microsoft.Data.Sqlite;

namespace QL.World.Services;

/// <summary>
/// M11: issues human-readable, unique-on-disk entry codes of the form
/// <c>YYYYMMDD-&lt;hash前6位&gt;-&lt;序号&gt;</c> (e.g. 20261009-a1b2c3-001).
/// The sequence number comes from the <c>entry_code_counter</c> table
/// (schema v9), keyed by (date, hash_prefix). The increment is a single
/// SQLite UPSERT ... RETURNING statement, so two concurrent importers
/// serialize on the primary-key conflict and never receive the same
/// counter value. The reservation runs in the caller's transaction, so
/// a rolled-back batch also undoes the increment — codes stay dense for
/// rows that actually commit while remaining globally unique.
/// </summary>
public static class EntryCodeGenerator
{
    /// <summary>Number of leading content-hash characters in the code.</summary>
    public const int HashPrefixLength = 6;

    /// <summary>Zero-padded width of the per-day sequence (10..999, then grows naturally).</summary>
    public const int SequenceWidth = 3;

    /// <summary>
    /// Atomically reserves and returns the next code for the given date
    /// and content hash. Must be called inside the caller's transaction so
    /// a batch rollback also undoes the counter reservation intent (the
    /// row simply keeps its incremented value — see class remarks on gaps).
    /// </summary>
    public static string Next(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateOnly date,
        string contentHash)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);
        if (contentHash.Length < HashPrefixLength)
            throw new ArgumentException(
                $"Content hash must be at least {HashPrefixLength} characters.",
                nameof(contentHash));

        string dateKey = date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        string prefix = contentHash[..HashPrefixLength].ToLowerInvariant();

        long counter;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO entry_code_counter (date, hash_prefix, counter)
                VALUES ($date, $prefix, 1)
                ON CONFLICT(date, hash_prefix) DO UPDATE
                    SET counter = counter + 1
                RETURNING counter;
                """;
            command.Parameters.AddWithValue("$date", dateKey);
            command.Parameters.AddWithValue("$prefix", prefix);
            counter = Convert.ToInt64(command.ExecuteScalar());
        }

        return $"{dateKey}-{prefix}-{counter.ToString($"D{SequenceWidth}", CultureInfo.InvariantCulture)}";
    }
}
