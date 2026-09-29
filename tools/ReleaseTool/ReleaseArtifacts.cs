using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QL.World.ReleaseTool;

/// <summary>
/// 输入参数：调用方（build.ps1 或测试）告诉 ReleaseArtifacts 去哪里取数据、写到哪里。
/// 所有路径都使用绝对路径；测试可注入 SourceArchivePath / ReleaseNotesPath /
/// UserGuidePath 跳过真实 git archive 与项目模板文件读取；GitInfoProvider 跳过真实 git 调用。
/// </summary>
public sealed record ReleaseInput(
    string StagingDir,
    string Version,
    string ProjectRoot,
    string DistRoot,
    string? SourceArchivePath = null,
    string? ReleaseNotesPath = null,
    string? UserGuidePath = null,
    IGitInfoProvider? GitInfoProvider = null
);

/// <summary>
/// 产出结果：DistDir = dist\StellarHub_v{ver}\；FilePathByRole 包含 5 个角色
/// （app/source/releaseNotes/userGuide/manifest）到绝对路径的映射；ManifestSha256
/// 是 manifest.json 文件自身的 SHA-256（用于 annotated tag message）。
/// </summary>
public sealed record ReleaseResult(
    string DistDir,
    IReadOnlyDictionary<string, string> FilePathByRole,
    string ManifestSha256,
    IReadOnlyList<string> ProducedFiles
);

/// <summary>
/// 五件套发布工具。满足 GEMINI.md §7 契约：
/// 历史版本永不覆盖、manifest 最后原子落地、annotated tag message 含 release-manifest-sha256。
/// </summary>
public static class ReleaseArtifacts
{
    public const string AppRole = "app";
    public const string SourceRole = "source";
    public const string ReleaseNotesRole = "releaseNotes";
    public const string UserGuideRole = "userGuide";
    public const string ManifestRole = "manifest";

    /// <summary>
    /// manifest.json 序列化选项：camelCase、缩进 2、忽略 null 字段、
    /// 非 ASCII（中文文件名）不转义。公开以便契约测试复用同一份选项计算 manifestSha256。
    /// </summary>
    public static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        // 属性顺序按声明顺序：version → commitSha → commitShortSha → branch →
        // buildTimeUtc → files → manifestSha256
        // System.Text Json 默认就保留声明顺序，无需额外配置。
    };

    private const string AppRoleName = "app";
    private const string SourceRoleName = "source";
    private const string ReleaseNotesRoleName = "releaseNotes";
    private const string UserGuideRoleName = "userGuide";

    /// <summary>
    /// 便利入口：从 staging 目录直接产出五件套到 distRoot\StellarHub_v{version}\。
    /// build.ps1 用此方法签名；测试一般直接构造 ReleaseInput 调 Produce 以注入更多参数。
    /// </summary>
    public static ReleaseResult ProduceFromStaging(
        string stagingDir,
        string version,
        string projectRoot,
        string distRoot)
    {
        var input = new ReleaseInput(stagingDir, version, projectRoot, distRoot);
        return Produce(input);
    }

    /// <summary>
    /// 主入口：产五件套到 DistRoot\StellarHub_v{version}\；该版本子目录已存在则抛
    /// InvalidOperationException（历史版本永不覆盖）。所有文件先写到 DistRoot 下的
    /// .tmp-{version}-{guid} 兄弟目录，全部成功后 Directory.Move 到最终位置（原子落地）。
    /// 任一失败清理临时目录后重抛。
    /// </summary>
    public static ReleaseResult Produce(ReleaseInput input, TextWriter? log = null)
    {
        if (input is null) throw new ArgumentNullException(nameof(input));
        if (string.IsNullOrWhiteSpace(input.StagingDir))
            throw new ArgumentException("StagingDir is required.", nameof(input));
        if (string.IsNullOrWhiteSpace(input.Version))
            throw new ArgumentException("Version is required.", nameof(input));
        if (string.IsNullOrWhiteSpace(input.ProjectRoot))
            throw new ArgumentException("ProjectRoot is required.", nameof(input));
        if (string.IsNullOrWhiteSpace(input.DistRoot))
            throw new ArgumentException("DistRoot is required.", nameof(input));

        if (!Directory.Exists(input.StagingDir))
            throw new DirectoryNotFoundException($"Staging directory not found: {input.StagingDir}");

        if (!VersionMatchesPattern(input.Version))
            throw new ArgumentException(
                $"Version must be major.minor.patch (found: '{input.Version}').", nameof(input));

        string versionDirName = $"StellarHub_v{input.Version}";
        string versionDir = Path.GetFullPath(Path.Combine(input.DistRoot, versionDirName));
        string distRootFull = Path.GetFullPath(input.DistRoot);

        if (Directory.Exists(versionDir))
        {
            throw new InvalidOperationException(
                $"dist 已存在版本 {input.Version} 的发布，禁止覆盖: {versionDir}");
        }

        Directory.CreateDirectory(distRootFull);
        string tmpDir = Path.Combine(
            distRootFull, $".tmp-{input.Version}-{Guid.NewGuid():N}");

        var produced = new Dictionary<string, string>(StringComparer.Ordinal);
        var fileArtifacts = new List<(string Role, string FileName, string AbsPath)>();

        try
        {
            Directory.CreateDirectory(tmpDir);
            log?.WriteLine($"[ReleaseTool] v{input.Version} : temp work dir = {tmpDir}");

            // ── 件 1: app.zip ── staging 全部内容打包（递归，含 run.bat / run-hidden.vbs
            //    / run.ps1 等启动脚本；ZipFile.CreateFromDirectory 默认不收集空目录）。
            string appFileName = $"StellarHub_v{input.Version}.zip";
            string appPath = Path.Combine(tmpDir, appFileName);
            WriteAppZip(input.StagingDir, appPath, log);
            fileArtifacts.Add((AppRoleName, appFileName, appPath));

            // ── 件 2: source.zip ── git archive --format=zip HEAD；
            //    测试通过 ReleaseInput.SourceArchivePath 注入预制 zip 直接复制。
            string sourceFileName = $"StellarHub_v{input.Version}_source.zip";
            string sourcePath = Path.Combine(tmpDir, sourceFileName);
            if (!string.IsNullOrEmpty(input.SourceArchivePath))
            {
                if (!File.Exists(input.SourceArchivePath))
                    throw new FileNotFoundException(
                        $"Injected source archive not found: {input.SourceArchivePath}",
                        input.SourceArchivePath);
                File.Copy(input.SourceArchivePath, sourcePath, overwrite: true);
                log?.WriteLine(
                    $"[ReleaseTool] source.zip copied from injected path: {input.SourceArchivePath}");
            }
            else
            {
                WriteSourceArchive(input.ProjectRoot, sourcePath, log);
            }
            fileArtifacts.Add((SourceRoleName, sourceFileName, sourcePath));

            // ── 件 3: 更新公告.txt ── 模板来自 docs/release/RELEASE_NOTES.md；
            //    ReleaseNotesPath 注入时必须存在（严格），null 时回退默认路径，
            //    默认路径仍不存在则生成最小化占位模板。
            string releaseNotesFileName = $"StellarHub_v{input.Version}_更新公告.txt";
            string releaseNotesPath = Path.Combine(tmpDir, releaseNotesFileName);
            WriteReleaseNotes(releaseNotesPath, input.Version, input.ReleaseNotesPath,
                input.ProjectRoot, log);
            fileArtifacts.Add((ReleaseNotesRoleName, releaseNotesFileName, releaseNotesPath));

            // ── 件 4: 使用说明.txt ── 内容来自 docs/USER_GUIDE.md 精简（M4-E 并行写它，
            //    这里只 Read 不 Write）；UserGuidePath 注入时必须存在（严格），
            //    null 时回退默认路径，默认路径仍不存在则生成最小占位文本
            //    （标题 + 一行 "详见 docs/USER_GUIDE.md"）。
            string userGuideFileName = $"StellarHub_v{input.Version}_使用说明.txt";
            string userGuidePath = Path.Combine(tmpDir, userGuideFileName);
            WriteUserGuide(userGuidePath, input.Version, input.UserGuidePath,
                input.ProjectRoot, log);
            fileArtifacts.Add((UserGuideRoleName, userGuideFileName, userGuidePath));

            // ── 计算件 1–4 各自 SHA-256 与文件大小（流式，64 hex 小写） ──
            var manifestFiles = new List<ManifestFileEntry>(fileArtifacts.Count);
            foreach (var (role, fileName, absPath) in fileArtifacts)
            {
                string sha = ComputeSha256(absPath);
                long size = new FileInfo(absPath).Length;
                manifestFiles.Add(new ManifestFileEntry
                {
                    Role = role,
                    FileName = fileName,
                    Sha256 = sha,
                    SizeBytes = size
                });
            }

            // ── 件 5: manifest.json ── 最后原子落地。
            //    计算顺序：先序列化（manifestSha256 字段为 null 被忽略）→ 算 SHA →
            //    回填 → 重新序列化 → 写临时文件 → File.Move 替换 → 返回 manifest 文件自身 SHA。
            var gitInfo = input.GitInfoProvider ?? new ProcessGitInfoProvider(input.ProjectRoot);
            var manifest = new Manifest
            {
                Version = input.Version,
                CommitSha = gitInfo.GetCommitSha(),
                CommitShortSha = gitInfo.GetCommitShortSha(),
                Branch = gitInfo.GetBranch(),
                BuildTimeUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
                Files = manifestFiles,
                ManifestSha256 = null  // 第一次序列化时被 WhenWritingNull 忽略
            };

            byte[] bytesWithoutField = JsonSerializer.SerializeToUtf8Bytes(
                manifest, ManifestJsonOptions);
            string shaNoField = BytesToHex(SHA256.HashData(bytesWithoutField));

            manifest.ManifestSha256 = shaNoField;
            byte[] bytesWithField = JsonSerializer.SerializeToUtf8Bytes(
                manifest, ManifestJsonOptions);

            string manifestFileName = $"StellarHub_v{input.Version}_manifest.json";
            string manifestPath = Path.Combine(tmpDir, manifestFileName);
            string manifestTmpPath = manifestPath + ".tmp";
            File.WriteAllBytes(manifestTmpPath, bytesWithField);
            File.Move(manifestTmpPath, manifestPath, overwrite: true);

            // 返回的 ManifestSha256 = manifest.json 文件自身的 SHA（含 manifestSha256 字段）
            string returnedManifestSha = ComputeSha256(manifestPath);
            fileArtifacts.Add((ManifestRole, manifestFileName, manifestPath));

            // ── 原子落地：Directory.Move(tmpDir, versionDir) ──
            // 任一上述步骤抛异常都会进入 catch 删除 tmpDir，versionDir 永不创建。
            Directory.Move(tmpDir, versionDir);

            foreach (var (role, fileName, _) in fileArtifacts)
            {
                produced[role] = Path.Combine(versionDir, fileName);
            }

            var producedList = produced.Values
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();

            log?.WriteLine($"[ReleaseTool] v{input.Version} : release produced at {versionDir}");
            log?.WriteLine($"[ReleaseTool] v{input.Version} : manifest sha256 = {returnedManifestSha}");

            return new ReleaseResult(versionDir, produced, returnedManifestSha, producedList);
        }
        catch
        {
            TryDeleteDirectory(tmpDir);
            throw;
        }
    }

    private static bool VersionMatchesPattern(string version)
        => System.Text.RegularExpressions.Regex.IsMatch(
            version, @"^\d+\.\d+\.\d+$");

    private static void WriteAppZip(string stagingDir, string appZipPath, TextWriter? log)
    {
        // includeBaseDirectory: false → entries 相对 stagingDir 内容（不含 stagingDir 名）。
        // ZipFile.CreateFromDirectory 默认不收集空目录。
        ZipFile.CreateFromDirectory(
            stagingDir, appZipPath,
            CompressionLevel.Optimal,
            includeBaseDirectory: false);
        log?.WriteLine($"[ReleaseTool] app.zip created from {stagingDir} -> {appZipPath}");
    }

    private static void WriteSourceArchive(string projectRoot, string outputPath, TextWriter? log)
    {
        int code = GitRunner.Run(
            projectRoot,
            new[] { "archive", "--format=zip", $"--output={outputPath}", "HEAD" },
            out string stdout, out string stderr);
        if (code != 0)
        {
            throw new InvalidOperationException(
                $"git archive failed (exit {code}): {stderr}{stdout}");
        }
        log?.WriteLine($"[ReleaseTool] source.zip created via git archive at {outputPath}");
    }

    private static void WriteReleaseNotes(
        string destPath, string version, string? sourcePath, string projectRoot, TextWriter? log)
    {
        string content;
        if (!string.IsNullOrEmpty(sourcePath))
        {
            // 注入路径必须存在（严格）—— 测试 Atomic_OnFailure_CleansTmp 用此分支抛异常。
            if (!File.Exists(sourcePath))
                throw new FileNotFoundException(
                    $"Release notes file not found: {sourcePath}", sourcePath);
            content = File.ReadAllText(sourcePath);
        }
        else
        {
            // 默认模板路径：{projectRoot}\docs\release\RELEASE_NOTES.md
            string defaultPath = Path.Combine(projectRoot, "docs", "release", "RELEASE_NOTES.md");
            if (File.Exists(defaultPath))
            {
                content = File.ReadAllText(defaultPath);
            }
            else
            {
                content = BuildMinimalReleaseNotes(version);
            }
        }

        // 替换版本占位符（不实现 markdown→txt 转换，模板就是模板）
        content = ReplaceVersionPlaceholders(content, version);
        File.WriteAllText(destPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        log?.WriteLine($"[ReleaseTool] release notes written to {destPath}");
    }

    private static void WriteUserGuide(
        string destPath, string version, string? sourcePath, string projectRoot, TextWriter? log)
    {
        string content;
        if (!string.IsNullOrEmpty(sourcePath))
        {
            if (!File.Exists(sourcePath))
                throw new FileNotFoundException(
                    $"User guide file not found: {sourcePath}", sourcePath);
            content = File.ReadAllText(sourcePath);
        }
        else
        {
            // 默认来源：{projectRoot}\docs\USER_GUIDE.md（M4-E 并行写它，本里程碑不创建）
            string defaultPath = Path.Combine(projectRoot, "docs", "USER_GUIDE.md");
            if (File.Exists(defaultPath))
            {
                // 精简生成：当前实现直接复用 md 内容；M4-Z 真实发布时可增强为 markdown→txt 转换。
                content = File.ReadAllText(defaultPath);
            }
            else
            {
                content = BuildMinimalUserGuide(version);
            }
        }

        content = ReplaceVersionPlaceholders(content, version);
        File.WriteAllText(destPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        log?.WriteLine($"[ReleaseTool] user guide written to {destPath}");
    }

    private static string BuildMinimalReleaseNotes(string version)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"千星汇图 v{version} 更新公告");
        sb.AppendLine();
        sb.AppendLine($"发布日期: {DateTime.UtcNow:yyyy-MM-dd}");
        sb.AppendLine();
        sb.AppendLine("## 亮点");
        sb.AppendLine();
        sb.AppendLine("（待 M4-Z 真实发布时填写）");
        sb.AppendLine();
        sb.AppendLine("## 新功能");
        sb.AppendLine();
        sb.AppendLine("（待 M4-Z 真实发布时填写）");
        sb.AppendLine();
        sb.AppendLine("## 修复");
        sb.AppendLine();
        sb.AppendLine("（待 M4-Z 真实发布时填写）");
        sb.AppendLine();
        sb.AppendLine("## 已知问题");
        sb.AppendLine();
        sb.AppendLine("（待 M4-Z 真实发布时填写）");
        sb.AppendLine();
        return sb.ToString();
    }

    private static string BuildMinimalUserGuide(string version)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"千星汇图 v{version} 使用说明");
        sb.AppendLine();
        sb.AppendLine("详见 docs/USER_GUIDE.md");
        sb.AppendLine();
        return sb.ToString();
    }

    private static string ReplaceVersionPlaceholders(string content, string version)
    {
        return content
            .Replace("v{x.y.z}", $"v{version}")
            .Replace("{x.y.z}", version);
    }

    private static string ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var hash = SHA256.HashData(stream);
        return BytesToHex(hash);
    }

    private static string BytesToHex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
            sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    private static void TryDeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // 失败清理是 best effort；调用方抛出的原始异常才是关键。
        }
    }
}

/// <summary>manifest.json 的 JSON 模型。属性顺序即 JSON 字段顺序。</summary>
public sealed class Manifest
{
    public string Version { get; set; } = string.Empty;
    public string CommitSha { get; set; } = string.Empty;
    public string CommitShortSha { get; set; } = string.Empty;
    public string Branch { get; set; } = string.Empty;
    public string BuildTimeUtc { get; set; } = string.Empty;
    public IList<ManifestFileEntry> Files { get; set; } = new List<ManifestFileEntry>();
    public string? ManifestSha256 { get; set; }
}

/// <summary>manifest.files 数组项模型。</summary>
public sealed class ManifestFileEntry
{
    public string Role { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
}
