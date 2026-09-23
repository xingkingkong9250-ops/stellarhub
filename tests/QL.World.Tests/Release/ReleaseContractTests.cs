using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QL.World.ReleaseTool;
using Xunit;

namespace QL.World.Tests.Release;

/// <summary>
/// M4-F 发布契约测试：验证 ReleaseArtifacts.Produce 满足 GEMINI.md §7 五件套契约
/// （命名、manifest schema、SHA 一致性、历史版本永不覆盖、原子落地、注入路径支持）。
/// 全部使用临时目录，禁用真实 git 调用（注入 FakeGitInfoProvider）。
/// </summary>
public sealed class ReleaseContractTests
{
    private const string TestVersion = "0.5.0";

    [Fact]
    public void Produce_ProducesAllFiveFilesWithCorrectNames()
    {
        using var fixture = new ReleaseFixture();
        var input = fixture.BuildInput();

        var result = ReleaseArtifacts.Produce(input);

        Assert.True(Directory.Exists(result.DistDir));
        Assert.Equal(
            Path.Combine(fixture.DistRoot, $"QL World_v{TestVersion}"),
            result.DistDir);

        // 5 个角色齐全
        Assert.Equal(5, result.FilePathByRole.Count);
        Assert.True(result.FilePathByRole.ContainsKey(ReleaseArtifacts.AppRole));
        Assert.True(result.FilePathByRole.ContainsKey(ReleaseArtifacts.SourceRole));
        Assert.True(result.FilePathByRole.ContainsKey(ReleaseArtifacts.ReleaseNotesRole));
        Assert.True(result.FilePathByRole.ContainsKey(ReleaseArtifacts.UserGuideRole));
        Assert.True(result.FilePathByRole.ContainsKey(ReleaseArtifacts.ManifestRole));

        // 文件命名规范：QL World_v{x.y.z}[role].ext
        Assert.Equal($"QL World_v{TestVersion}.zip",
            Path.GetFileName(result.FilePathByRole[ReleaseArtifacts.AppRole]));
        Assert.Equal($"QL World_v{TestVersion}_source.zip",
            Path.GetFileName(result.FilePathByRole[ReleaseArtifacts.SourceRole]));
        Assert.Equal($"QL World_v{TestVersion}_更新公告.txt",
            Path.GetFileName(result.FilePathByRole[ReleaseArtifacts.ReleaseNotesRole]));
        Assert.Equal($"QL World_v{TestVersion}_使用说明.txt",
            Path.GetFileName(result.FilePathByRole[ReleaseArtifacts.UserGuideRole]));
        Assert.Equal($"QL World_v{TestVersion}_manifest.json",
            Path.GetFileName(result.FilePathByRole[ReleaseArtifacts.ManifestRole]));

        // 所有文件都实际存在
        foreach (var path in result.ProducedFiles)
            Assert.True(File.Exists(path), $"Missing produced file: {path}");
    }

    [Fact]
    public void Produce_ManifestHasCorrectSchema()
    {
        using var fixture = new ReleaseFixture();
        var input = fixture.BuildInput();

        var result = ReleaseArtifacts.Produce(input);

        string manifestPath = result.FilePathByRole[ReleaseArtifacts.ManifestRole];
        var doc = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("version", out var ver));
        Assert.Equal(TestVersion, ver.GetString());
        Assert.True(root.TryGetProperty("commitSha", out var sha));
        Assert.False(string.IsNullOrEmpty(sha.GetString()));
        Assert.True(root.TryGetProperty("commitShortSha", out var shortSha));
        Assert.False(string.IsNullOrEmpty(shortSha.GetString()));
        Assert.True(root.TryGetProperty("branch", out var branch));
        Assert.False(string.IsNullOrEmpty(branch.GetString()));
        Assert.True(root.TryGetProperty("buildTimeUtc", out var time));
        Assert.False(string.IsNullOrEmpty(time.GetString()));
        Assert.True(root.TryGetProperty("files", out var files));
        Assert.Equal(4, files.GetArrayLength());

        foreach (var file in files.EnumerateArray())
        {
            Assert.True(file.TryGetProperty("role", out var role));
            Assert.False(string.IsNullOrEmpty(role.GetString()));
            Assert.True(file.TryGetProperty("fileName", out var name));
            Assert.False(string.IsNullOrEmpty(name.GetString()));
            Assert.True(file.TryGetProperty("sha256", out var hash));
            Assert.Matches(@"^[0-9a-f]{64}$", hash.GetString()!);
            Assert.True(file.TryGetProperty("sizeBytes", out var size));
            Assert.True(size.GetInt64() > 0);
        }

        Assert.True(root.TryGetProperty("manifestSha256", out var manifestSha));
        Assert.Matches(@"^[0-9a-f]{64}$", manifestSha.GetString()!);
    }

    [Fact]
    public void Produce_ManifestShaMatchesActualFiles()
    {
        using var fixture = new ReleaseFixture();
        var input = fixture.BuildInput();

        var result = ReleaseArtifacts.Produce(input);

        string manifestPath = result.FilePathByRole[ReleaseArtifacts.ManifestRole];
        var doc = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        var root = doc.RootElement;

        // 每个 file 的 sha256 必须等于磁盘实际文件 SHA-256
        foreach (var file in root.GetProperty("files").EnumerateArray())
        {
            string role = file.GetProperty("role").GetString()!;
            string declaredSha = file.GetProperty("sha256").GetString()!;
            long declaredSize = file.GetProperty("sizeBytes").GetInt64();
            string actualPath = result.FilePathByRole[role];

            string actualSha = ComputeSha256(actualPath);
            Assert.Equal(declaredSha, actualSha);
            Assert.Equal(declaredSize, new FileInfo(actualPath).Length);
        }

        // manifestSha256 字段 = manifest.json 不含该字段时的字节 SHA-256
        string parsedFieldSha = root.GetProperty("manifestSha256").GetString()!;
        var manifestWithoutField = new Manifest
        {
            Version = root.GetProperty("version").GetString()!,
            CommitSha = root.GetProperty("commitSha").GetString()!,
            CommitShortSha = root.GetProperty("commitShortSha").GetString()!,
            Branch = root.GetProperty("branch").GetString()!,
            BuildTimeUtc = root.GetProperty("buildTimeUtc").GetString()!,
            Files = ParseFileEntries(root.GetProperty("files")),
            ManifestSha256 = null
        };
        byte[] bytesWithoutField = JsonSerializer.SerializeToUtf8Bytes(
            manifestWithoutField, ReleaseArtifacts.ManifestJsonOptions);
        string computedSha = ComputeSha256Bytes(SHA256.HashData(bytesWithoutField));
        Assert.Equal(parsedFieldSha, computedSha);

        // 返回的 ManifestSha256 = manifest.json 文件自身的 SHA
        string actualManifestSha = ComputeSha256(manifestPath);
        Assert.Equal(actualManifestSha, result.ManifestSha256);
    }

    [Fact]
    public void Produce_DistAlreadyExists_Throws()
    {
        using var fixture = new ReleaseFixture();
        var input = fixture.BuildInput();

        // 第一次 Produce 成功
        var first = ReleaseArtifacts.Produce(input);
        Assert.True(Directory.Exists(first.DistDir));

        // 第二次同 DistRoot 同版本 → 抛 InvalidOperationException（历史永不覆盖）
        var ex = Assert.Throws<InvalidOperationException>(
            () => ReleaseArtifacts.Produce(input));
        Assert.Contains("禁止覆盖", ex.Message);
        // 原文件仍然存在
        Assert.True(Directory.Exists(first.DistDir));
        Assert.True(File.Exists(first.FilePathByRole[ReleaseArtifacts.ManifestRole]));
    }

    [Fact]
    public void Produce_DryRun_LeavesNoResidue()
    {
        // DryRun 通过临时 DistRoot 实现：测试完成后 DistRoot 整体删除，
        // 不污染真实 dist\。第二次 Produce 在新 DistRoot 下同 version 也能成功。
        using var fixture1 = new ReleaseFixture();
        var input1 = fixture1.BuildInput();

        var result1 = ReleaseArtifacts.Produce(input1);
        Assert.True(Directory.Exists(result1.DistDir));
        // DistRoot 下只含该版本子目录
        var entries = Directory.GetFileSystemEntries(fixture1.DistRoot);
        Assert.Single(entries);
        Assert.Equal(result1.DistDir, entries[0]);
        Assert.False(Directory.Exists(
            Path.Combine(fixture1.DistRoot, ".tmp-anything")));

        // 删除子目录后，同 DistRoot 同版本可再次 Produce（重新创建 tmpDir 并落地）
        Directory.Delete(result1.DistDir, recursive: true);
        var result2 = ReleaseArtifacts.Produce(input1);
        Assert.True(Directory.Exists(result2.DistDir));
        Assert.True(File.Exists(result2.FilePathByRole[ReleaseArtifacts.ManifestRole]));

        // 真实 dist\ 不应被创建（DistRoot 在 temp 路径下）
        Assert.False(Directory.Exists(
            Path.Combine(Path.GetTempPath(), "QLWorld-real-dist")));
    }

    [Fact]
    public void Produce_AppZipContainsStagingContent()
    {
        using var fixture = new ReleaseFixture();
        var input = fixture.BuildInput();

        var result = ReleaseArtifacts.Produce(input);

        string appZipPath = result.FilePathByRole[ReleaseArtifacts.AppRole];
        using var archive = ZipFile.OpenRead(appZipPath);

        // 关键启动脚本应被打包（staging fixture 含 run.bat / run-hidden.vbs / run.ps1）
        var entryNames = archive.Entries.Select(e => e.FullName).ToHashSet();
        Assert.Contains("run.bat", entryNames);
        Assert.Contains("run-hidden.vbs", entryNames);
        Assert.Contains("run.ps1", entryNames);
        Assert.Contains("QL World.dll", entryNames);
        Assert.Contains("QL World.exe", entryNames);

        // 内容字节一致：run.bat 字节相等
        var runBatEntry = archive.GetEntry("run.bat")!;
        Assert.NotNull(runBatEntry);
        string originalRunBat = File.ReadAllText(Path.Combine(fixture.StagingDir, "run.bat"));
        using var reader = new StreamReader(runBatEntry.Open(), Encoding.UTF8);
        string zipRunBat = reader.ReadToEnd();
        Assert.Equal(originalRunBat, zipRunBat);
    }

    [Fact]
    public void Produce_SourceArchive_UsesInjectedPath_WhenProvided()
    {
        using var fixture = new ReleaseFixture();
        // 预制一个 source.zip，含一个标记文件 SOURCE_MARKER.txt
        string markerDir = Path.Combine(fixture.OwnTemp, "source-payload");
        Directory.CreateDirectory(markerDir);
        File.WriteAllText(Path.Combine(markerDir, "SOURCE_MARKER.txt"),
            "this is a fake source archive marker");
        string injectedSourceZip = Path.Combine(fixture.OwnTemp, "injected-source.zip");
        ZipFile.CreateFromDirectory(markerDir, injectedSourceZip,
            CompressionLevel.Optimal, includeBaseDirectory: false);

        var input = fixture.BuildInput(sourceArchivePath: injectedSourceZip);

        var result = ReleaseArtifacts.Produce(input);

        // 件 2 字节与注入的 zip 一致
        string producedSourcePath = result.FilePathByRole[ReleaseArtifacts.SourceRole];
        byte[] injectedBytes = File.ReadAllBytes(injectedSourceZip);
        byte[] producedBytes = File.ReadAllBytes(producedSourcePath);
        Assert.Equal(injectedBytes, producedBytes);

        // 解压含标记文件
        using var archive = ZipFile.OpenRead(producedSourcePath);
        var names = archive.Entries.Select(e => e.FullName).ToHashSet();
        Assert.Contains("SOURCE_MARKER.txt", names);
    }

    [Fact]
    public void Produce_Atomic_OnFailure_CleansTmp()
    {
        using var fixture = new ReleaseFixture();
        // 注入不存在的 release notes 路径 → WriteReleaseNotes 抛 FileNotFoundException
        // （注入路径必须存在，否则严格抛错）
        string badPath = Path.Combine(fixture.OwnTemp, "does-not-exist.md");
        var input = fixture.BuildInput(releaseNotesPath: badPath);

        var ex = Assert.Throws<FileNotFoundException>(
            () => ReleaseArtifacts.Produce(input));
        Assert.Contains(badPath, ex.Message);

        // 失败后：版本子目录永不存在，DistRoot 下无 .tmp-* 残留
        string versionDir = Path.Combine(fixture.DistRoot, $"QL World_v{TestVersion}");
        Assert.False(Directory.Exists(versionDir));

        if (Directory.Exists(fixture.DistRoot))
        {
            var residue = Directory.GetFileSystemEntries(fixture.DistRoot);
            Assert.All(residue, p =>
                Assert.False(Path.GetFileName(p).StartsWith(".tmp-",
                    StringComparison.OrdinalIgnoreCase)));
            Assert.Empty(residue);
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return ComputeSha256Bytes(SHA256.HashData(stream));
    }

    private static string ComputeSha256Bytes(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
            sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    private static IList<ManifestFileEntry> ParseFileEntries(JsonElement files)
    {
        var list = new List<ManifestFileEntry>();
        foreach (var f in files.EnumerateArray())
        {
            list.Add(new ManifestFileEntry
            {
                Role = f.GetProperty("role").GetString()!,
                FileName = f.GetProperty("fileName").GetString()!,
                Sha256 = f.GetProperty("sha256").GetString()!,
                SizeBytes = f.GetProperty("sizeBytes").GetInt64()
            });
        }
        return list;
    }

    /// <summary>
    /// 测试夹具：管理一个临时根目录、一个 fake staging（含 run.bat / run.ps1 /
    /// run-hidden.vbs / QL World.dll / QL World.exe）、一个 fake DistRoot、一个
    /// FakeGitInfoProvider、可选的注入路径。Dispose 时统一清理。
    /// </summary>
    private sealed class ReleaseFixture : IDisposable
    {
        private readonly string _ownTemp;

        public ReleaseFixture()
        {
            _ownTemp = Path.Combine(
                Path.GetTempPath(),
                "ql-world-release-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_ownTemp);
            OwnTemp = _ownTemp;

            StagingDir = Path.Combine(_ownTemp, "staging", $"QL World_v{TestVersion}");
            Directory.CreateDirectory(StagingDir);
            BuildFakeStaging(StagingDir);

            DistRoot = Path.Combine(_ownTemp, "dist");

            ReleaseNotesPath = Path.Combine(_ownTemp, "release-notes.md");
            File.WriteAllText(ReleaseNotesPath,
                $"# v{{x.y.z}} 更新公告\n\n## 亮点\n- 测试占位\n");
            UserGuidePath = Path.Combine(_ownTemp, "USER_GUIDE.md");
            File.WriteAllText(UserGuidePath,
                $"# QL World v{{x.y.z}} 用户指南\n\n## 安装\n- 解压即可运行\n");

            // 默认预制 source.zip：避免测试依赖真实 git 仓库（测试 ProjectRoot 在 temp 下
            // 不是 git 工作区，git archive 会失败）。测试可显式传 sourceArchivePath 覆盖。
            DefaultSourceArchivePath = Path.Combine(_ownTemp, "default-source.zip");
            string defaultSourceDir = Path.Combine(_ownTemp, "default-source-payload");
            Directory.CreateDirectory(defaultSourceDir);
            File.WriteAllText(Path.Combine(defaultSourceDir, "README.md"),
                "# fake source archive for default fixture");
            ZipFile.CreateFromDirectory(defaultSourceDir, DefaultSourceArchivePath,
                CompressionLevel.Optimal, includeBaseDirectory: false);

            GitInfoProvider = new FakeGitInfoProvider();
        }

        public string OwnTemp { get; }
        public string StagingDir { get; }
        public string DistRoot { get; }
        public string ReleaseNotesPath { get; }
        public string UserGuidePath { get; }
        public string DefaultSourceArchivePath { get; }
        public IGitInfoProvider GitInfoProvider { get; }

        public ReleaseInput BuildInput(
            string? sourceArchivePath = null,
            string? releaseNotesPath = null)
        {
            return new ReleaseInput(
                StagingDir,
                TestVersion,
                _ownTemp,
                DistRoot,
                sourceArchivePath ?? DefaultSourceArchivePath,
                releaseNotesPath ?? ReleaseNotesPath,
                UserGuidePath,
                GitInfoProvider);
        }

        public void Dispose()
        {
            try { Directory.Delete(_ownTemp, recursive: true); }
            catch (IOException) { /* best effort temp cleanup */ }
        }

        private static void BuildFakeStaging(string dir)
        {
            // 模拟一个最小可发布 staging（self-contained win-x64 输出）
            File.WriteAllText(Path.Combine(dir, "run.bat"),
                "@echo off\nstart \"\" \"QL World.exe\"\n");
            File.WriteAllText(Path.Combine(dir, "run-hidden.vbs"),
                "Set WshShell = CreateObject(\"WScript.Shell\")\n" +
                $"WshShell.Run \"run.bat\", 0, False\n");
            File.WriteAllText(Path.Combine(dir, "run.ps1"),
                "$ErrorActionPreference = 'Stop'\nStart-Process -FilePath 'QL World.exe'\n");

            // 假 dll/exe（非真实 PE，只占字节）
            File.WriteAllBytes(Path.Combine(dir, "QL World.dll"),
                new byte[1024]);
            File.WriteAllBytes(Path.Combine(dir, "QL World.exe"),
                new byte[2048]);
            File.WriteAllBytes(Path.Combine(dir, "QL World.runtimeconfig.json"),
                Encoding.UTF8.GetBytes("{}"));
            File.WriteAllBytes(Path.Combine(dir, "apphost.dll"),
                new byte[512]);

            // 嵌套目录，验证递归打包
            Directory.CreateDirectory(Path.Combine(dir, "runtimes", "win-x64", "native"));
            File.WriteAllBytes(
                Path.Combine(dir, "runtimes", "win-x64", "native", "onnxruntime.dll"),
                new byte[256]);
        }
    }

    /// <summary>
    /// 假 Git 信息提供者：避免测试依赖真实 git 仓库存在。
    /// </summary>
    private sealed class FakeGitInfoProvider : IGitInfoProvider
    {
        public string GetCommitSha() => "0123456789abcdef0123456789abcdef01234567";
        public string GetCommitShortSha() => "0123456";
        public string GetBranch() => "master";
    }
}
