using QL.World.Services;
using QL.World.Tests.Support;

namespace QL.World.Tests;

public sealed class EntryCodeGeneratorTests
{
    private const string HashA = "a1b2c3d4e5f60718293a4b5c6d7e8f90";
    private const string HashB = "fedcba9876543210fedcba9876543210";
    private static readonly DateOnly Day = new(2026, 10, 9);

    [Fact]
    public void First_code_for_day_and_prefix_is_001()
    {
        using var library = new TestLibrary();

        string code = library.Db.RunInTransaction((conn, tx) =>
            EntryCodeGenerator.Next(conn, tx, Day, HashA));

        Assert.Equal("20261009-a1b2c3-001", code);
    }

    [Fact]
    public void Same_day_same_prefix_increments_across_committed_transactions()
    {
        using var library = new TestLibrary();

        var codes = new List<string>();
        for (int i = 0; i < 3; i++)
        {
            codes.Add(library.Db.RunInTransaction((conn, tx) =>
                EntryCodeGenerator.Next(conn, tx, Day, HashA)));
        }

        Assert.Equal(
            ["20261009-a1b2c3-001", "20261009-a1b2c3-002", "20261009-a1b2c3-003"],
            codes);
    }

    [Fact]
    public void Different_prefix_restarts_at_001()
    {
        using var library = new TestLibrary();

        library.Db.RunInTransaction((conn, tx) =>
            EntryCodeGenerator.Next(conn, tx, Day, HashA));

        string codeB = library.Db.RunInTransaction((conn, tx) =>
            EntryCodeGenerator.Next(conn, tx, Day, HashB));

        Assert.Equal("20261009-fedcba-001", codeB);
    }

    [Fact]
    public void Different_day_restarts_at_001_for_same_prefix()
    {
        using var library = new TestLibrary();

        library.Db.RunInTransaction((conn, tx) =>
            EntryCodeGenerator.Next(conn, tx, Day, HashA));

        string nextDay = library.Db.RunInTransaction((conn, tx) =>
            EntryCodeGenerator.Next(conn, tx, new DateOnly(2026, 10, 10), HashA));

        Assert.Equal("20261010-a1b2c3-001", nextDay);
    }

    [Fact]
    public void Tenth_sequence_is_zero_padded_to_010_then_grows_naturally()
    {
        using var library = new TestLibrary();

        string tenth = "";
        for (int i = 0; i < 10; i++)
        {
            tenth = library.Db.RunInTransaction((conn, tx) =>
                EntryCodeGenerator.Next(conn, tx, Day, HashA));
        }

        Assert.Equal("20261009-a1b2c3-010", tenth);
    }

    [Fact]
    public void Rolled_back_transaction_releases_its_sequence_number()
    {
        using var library = new TestLibrary();

        // First reservation commits → 001.
        string committed = library.Db.RunInTransaction((conn, tx) =>
            EntryCodeGenerator.Next(conn, tx, Day, HashA));
        Assert.Equal("20261009-a1b2c3-001", committed);

        // Second reservation in a rolled-back transaction.
        try
        {
            library.Db.RunInTransaction((conn, tx) =>
            {
                EntryCodeGenerator.Next(conn, tx, Day, HashA);
                throw new InvalidOperationException("force rollback");
            });
        }
        catch (InvalidOperationException) { /* expected */ }

        // Counter increment rolled back too → next committed code is 002.
        string afterRollback = library.Db.RunInTransaction((conn, tx) =>
            EntryCodeGenerator.Next(conn, tx, Day, HashA));
        Assert.Equal("20261009-a1b2c3-002", afterRollback);
    }

    [Fact]
    public void Hash_shorter_than_prefix_length_throws()
    {
        using var library = new TestLibrary();

        Assert.Throws<ArgumentException>(() =>
            library.Db.RunInTransaction((conn, tx) =>
                EntryCodeGenerator.Next(conn, tx, Day, "abc")));
    }
}
