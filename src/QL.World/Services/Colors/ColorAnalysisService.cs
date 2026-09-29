using System.IO;
using Microsoft.Data.Sqlite;
using QL.World.Data;
using QL.World.Helpers;

namespace QL.World.Services.Colors;

/// <summary>
/// Batch dominant-color backfill over the library. Independent of the AI
/// mode (PRD: 颜色分析不依赖 AI 档位). Only fills NULL/empty dominant_colors
/// rows — never overwrites existing values; the extractor is deterministic
/// so backfill is idempotent. File errors are logged and skipped.
/// </summary>
public sealed class ColorAnalysisService
{
    private readonly DatabaseService _db;
    private readonly Action<string> _log;

    public ColorAnalysisService(DatabaseService db, Action<string>? log = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _log = log ?? (_ => { });
    }

    /// <summary>Backfills missing dominant_colors for up to batchLimit images. Returns count written.</summary>
    public async Task<int> RunPendingAsync(int batchLimit = 500, CancellationToken cancellationToken = default)
    {
        List<(long Id, string Path)> pending = GetPending(batchLimit);
        int written = 0;
        foreach ((long id, string path) in pending)
        {
            if (cancellationToken.IsCancellationRequested) break;
            if (await AnalyzeAsync(id, path, cancellationToken)) written++;
        }
        return written;
    }

    /// <summary>Analyzes one image; true when a value was written.</summary>
    public async Task<bool> AnalyzeAsync(long imageId, string filePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            _log($"[color] image {imageId} file missing: {filePath}");
            return false;
        }

        List<string> colors;
        try
        {
            await Task.Yield(); // leave caller sync context before heavy decode
            using SixLabors.ImageSharp.Image image =
                await SixLabors.ImageSharp.Image.LoadAsync(filePath, cancellationToken);
            colors = (List<string>)ColorExtractor.Extract(image);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or NotSupportedException or InvalidDataException
            or SixLabors.ImageSharp.UnknownImageFormatException
            or SixLabors.ImageSharp.InvalidImageContentException)
        {
            _log($"[color] image {imageId} decode failed: {ex.Message}");
            return false;
        }

        // Fill only when still NULL/empty (protects existing values).
        // U3-A: color_family (hue family of the first color) is written in the
        // same statement so the B1-5 color filter needs no extra pass.
        string? family = colors.Count > 0 ? ColorFamily.HexToFamily(colors[0]) : null;
        return _db.Run(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE images
                SET dominant_colors = $json,
                    color_family = $family
                WHERE id = $id AND (dominant_colors IS NULL OR dominant_colors = '');
                """;
            cmd.Parameters.AddWithValue("$json", System.Text.Json.JsonSerializer.Serialize(colors));
            cmd.Parameters.AddNullable("$family", family);
            cmd.Parameters.AddWithValue("$id", imageId);
            return cmd.ExecuteNonQuery() > 0;
        });
    }

    private List<(long Id, string Path)> GetPending(int limit) =>
        _db.Run(conn =>
        {
            using var command = conn.CreateCommand();
            command.CommandText = """
                SELECT id, file_path
                FROM images
                WHERE is_missing = 0
                  AND (dominant_colors IS NULL OR dominant_colors = '')
                ORDER BY id ASC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$limit", limit);
            var list = new List<(long, string)>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
                list.Add((reader.GetInt64(0), reader.GetString(1)));
            return list;
        });
}
