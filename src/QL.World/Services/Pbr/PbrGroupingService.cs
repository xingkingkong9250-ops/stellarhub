using System.IO;
using Microsoft.Data.Sqlite;
using QL.World.Data;
using QL.World.Helpers;
using QL.World.Models;

namespace QL.World.Services.Pbr;

/// <summary>Outcome of one grouping pass.</summary>
public sealed record PbrRescanReport(int ImagesScanned, int GroupsCreated, int MembersLinked);

/// <summary>
/// Groups texture maps into PBR material sets by editable suffix rules.
/// A group never spans directories (key = directory + base name) and needs
/// at least two members. Unmatched file names count as basecolor maps with
/// the full name as base ("T_Wall" + "T_Wall_Normal" form a group).
/// <see cref="RescanAll"/> rebuilds everything; <see cref="RescanDirectories"/>
/// limits the rebuild to affected directories (the import hook path).
/// </summary>
public sealed class PbrGroupingService
{
    private const int SQL_CHUNK = 500;

    private readonly DatabaseService _db;
    private readonly AppSettingsService _settings;
    private readonly Action<string>? _log;

    public PbrGroupingService(DatabaseService db, AppSettingsService settings, Action<string>? log = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _log = log;
    }

    public PbrRescanReport RescanAll()
    {
        List<ImageRow> images = LoadImages();
        Dictionary<string, List<PendingGroup>> grouped = GroupImages(images);
        return Rebuild(grouped, affectedIds: null, scannedCount: images.Count);
    }

    /// <summary>All groups with member counts, ordered by name (case-insensitive).</summary>
    public IReadOnlyList<PbrGroupInfo> GetGroups() =>
        _db.Run(conn =>
        {
            var groups = new List<PbrGroupInfo>();
            using var command = conn.CreateCommand();
            command.CommandText = """
                SELECT g.id, g.name, COUNT(m.image_id)
                FROM pbr_groups g
                LEFT JOIN pbr_group_images m ON m.group_id = g.id
                GROUP BY g.id, g.name
                ORDER BY g.name COLLATE NOCASE;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
                groups.Add(new PbrGroupInfo(
                    reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2)));
            return groups;
        });

    public PbrRescanReport RescanDirectories(IReadOnlyCollection<string> directories)
    {
        var dirSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string directory in directories)
        {
            string normalized = NormalizeDir(directory);
            if (normalized.Length > 0)
                dirSet.Add(normalized);
        }
        if (dirSet.Count == 0)
            return new PbrRescanReport(0, 0, 0);

        List<ImageRow> all = LoadImages();
        List<ImageRow> affected = all.Where(i => dirSet.Contains(i.Directory)).ToList();
        if (affected.Count == 0)
            return new PbrRescanReport(all.Count, 0, 0);

        Dictionary<string, List<PendingGroup>> grouped = GroupImages(affected);
        return Rebuild(grouped, affectedIds: [.. affected.Select(a => a.Id)], scannedCount: all.Count);
    }

    // ─── matching ─────────────────────────────────────────────────────────

    private sealed record ImageRow(long Id, string FilePath, string Directory);

    private sealed class PendingGroup
    {
        public required string BaseName { get; init; }
        public List<(long ImageId, string Role)> Members { get; } = [];
    }

    private Dictionary<string, List<PendingGroup>> GroupImages(List<ImageRow> images)
    {
        IReadOnlyList<PbrRule> rules = _settings.PbrRules;
        var byKey = new Dictionary<string, PendingGroup>(StringComparer.OrdinalIgnoreCase);

        foreach (ImageRow image in images)
        {
            string fileName = Path.GetFileNameWithoutExtension(image.FilePath);
            (string BaseName, string Role)? match = PbrRuleSet.MatchRole(fileName, rules);
            string baseName = match?.BaseName ?? fileName;
            string role = match?.Role ?? PbrRoles.BaseColor;

            string key = image.Directory + '\u0001' + baseName;
            if (!byKey.TryGetValue(key, out PendingGroup? group))
            {
                group = new PendingGroup { BaseName = baseName };
                byKey[key] = group;
            }
            group.Members.Add((image.Id, role));
        }

        var result = new Dictionary<string, List<PendingGroup>>(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, PendingGroup group) in byKey)
        {
            if (group.Members.Count < 2)
                continue;
            string dirKey = key[..key.IndexOf('\u0001')];
            if (!result.TryGetValue(dirKey, out List<PendingGroup>? list))
                result[dirKey] = list = [];
            list.Add(group);
        }
        foreach (List<PendingGroup> list in result.Values)
            list.Sort((a, b) => string.Compare(a.BaseName, b.BaseName, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    private List<ImageRow> LoadImages() =>
        _db.Run(conn =>
        {
            var images = new List<ImageRow>();
            using var command = conn.CreateCommand();
            command.CommandText = "SELECT id, file_path FROM images;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                string path = reader.GetString(1);
                images.Add(new ImageRow(
                    reader.GetInt64(0),
                    path,
                    NormalizeDir(Path.GetDirectoryName(path) ?? string.Empty)));
            }
            return images;
        });

    private static string NormalizeDir(string directory)
    {
        string full = Path.GetFullPath(directory);
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    // ─── persistence ──────────────────────────────────────────────────────

    private PbrRescanReport Rebuild(
        Dictionary<string, List<PendingGroup>> grouped,
        List<long>? affectedIds,
        int scannedCount)
    {
        int created = 0;
        int linked = 0;

        _db.RunInTransaction((conn, tx) =>
        {
            if (affectedIds is null)
            {
                Execute(conn, tx, "DELETE FROM pbr_group_images;");
                Execute(conn, tx, "DELETE FROM pbr_groups;");
            }
            else
            {
                // Remove memberships of affected images, then drop groups left empty.
                for (int offset = 0; offset < affectedIds.Count; offset += SQL_CHUNK)
                {
                    List<long> chunk = affectedIds.Skip(offset).Take(SQL_CHUNK).ToList();
                    var delete = conn.CreateCommand();
                    delete.Transaction = tx;
                    delete.CommandText =
                        "DELETE FROM pbr_group_images WHERE image_id IN ("
                        + string.Join(',', chunk.Select((_, i) => $"$id{i}")) + ");";
                    for (int i = 0; i < chunk.Count; i++)
                        delete.Parameters.AddWithValue($"$id{i}", chunk[i]);
                    delete.ExecuteNonQuery();
                }
                Execute(conn, tx,
                    "DELETE FROM pbr_groups WHERE id NOT IN "
                    + "(SELECT group_id FROM pbr_group_images);");
            }

            string now = DbTime.Now();
            foreach (List<PendingGroup> dirGroups in grouped.Values)
            {
                foreach (PendingGroup group in dirGroups)
                {
                    long groupId = InsertGroup(conn, tx, group.BaseName, now);
                    foreach ((long imageId, string role) in group.Members)
                    {
                        var member = conn.CreateCommand();
                        member.Transaction = tx;
                        member.CommandText =
                            "INSERT INTO pbr_group_images (group_id, image_id, role) "
                            + "VALUES ($gid, $iid, $role);";
                        member.Parameters.AddWithValue("$gid", groupId);
                        member.Parameters.AddWithValue("$iid", imageId);
                        member.Parameters.AddWithValue("$role", role);
                        member.ExecuteNonQuery();
                        linked++;
                    }
                    created++;
                }
            }
        });

        _log?.Invoke($"[PBR] rescan done: groups={created}, members={linked}.");

        return new PbrRescanReport(scannedCount, created, linked);
    }

    private static long InsertGroup(SqliteConnection conn, SqliteTransaction tx, string name, string now)
    {
        using var insert = conn.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText =
            "INSERT INTO pbr_groups (name, created_at, updated_at) VALUES ($name, $now, $now);"
            + "SELECT last_insert_rowid();";
        insert.Parameters.AddWithValue("$name", name);
        insert.Parameters.AddWithValue("$now", now);
        return Convert.ToInt64(insert.ExecuteScalar());
    }

    private static void Execute(SqliteConnection conn, SqliteTransaction tx, string sql)
    {
        using var command = conn.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
