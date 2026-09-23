using QL.World.ReleaseTool;

namespace QL.World.ReleaseTool;

/// <summary>
/// ReleaseTool CLI 入口：build.ps1 通过 PowerShell 5.1 兼容方式直接调用本 exe，
/// 避免 Add-Type 加载 .NET 10 程序集到 Windows PowerShell CLR 的不兼容问题。
/// 成功时最后一行输出 "MANIFEST_SHA:&lt;64hex&gt;" 供调用方解析 annotated tag 命令。
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            var opts = ParseArgs(args);
            if (opts.ShowHelp)
            {
                PrintHelp();
                return 0;
            }
            if (opts.Error is not null)
            {
                Console.Error.WriteLine(opts.Error);
                PrintHelp();
                return 2;
            }

            var input = new ReleaseInput(
                opts.StagingDir!,
                opts.Version!,
                opts.ProjectRoot!,
                opts.DistRoot!,
                opts.SourceArchivePath,
                opts.ReleaseNotesPath,
                opts.UserGuidePath,
                GitInfoProvider: null);

            var result = ReleaseArtifacts.Produce(input, Console.Out);
            // 最后一行输出固定格式，便于 build.ps1 用 -match 解析。
            Console.WriteLine($"MANIFEST_SHA:{result.ManifestSha256}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ReleaseTool] FATAL: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private sealed class Options
    {
        public string? StagingDir { get; set; }
        public string? Version { get; set; }
        public string? ProjectRoot { get; set; }
        public string? DistRoot { get; set; }
        public string? SourceArchivePath { get; set; }
        public string? ReleaseNotesPath { get; set; }
        public string? UserGuidePath { get; set; }
        public bool ShowHelp { get; set; }
        public string? Error { get; set; }
    }

    private static Options ParseArgs(string[] args)
    {
        var opts = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string? value = null;

            if (a.StartsWith("--"))
            {
                int eq = a.IndexOf('=');
                if (eq >= 0)
                {
                    value = a.Substring(eq + 1);
                    a = a.Substring(0, eq);
                }
            }

            // 两种取值：--key=value 或 --key value。
            // value 已通过 = 取到时直接用；否则取下一个非 -- 开头的参数。
            if (value is null && i + 1 < args.Length && !args[i + 1].StartsWith("--"))
            {
                value = args[i + 1];
                i++;
            }

            switch (a)
            {
                case "--staging": opts.StagingDir = value; break;
                case "--version": opts.Version = value; break;
                case "--project-root": opts.ProjectRoot = value; break;
                case "--dist-root": opts.DistRoot = value; break;
                case "--source-archive": opts.SourceArchivePath = value; break;
                case "--release-notes": opts.ReleaseNotesPath = value; break;
                case "--user-guide": opts.UserGuidePath = value; break;
                case "--help":
                case "-h": opts.ShowHelp = true; break;
                default:
                    if (opts.Error is null)
                        opts.Error = $"Unknown argument: {a}";
                    break;
            }
        }

        if (!opts.ShowHelp)
        {
            if (string.IsNullOrEmpty(opts.StagingDir))
                opts.Error ??= "--staging is required";
            if (string.IsNullOrEmpty(opts.Version))
                opts.Error ??= "--version is required";
            if (string.IsNullOrEmpty(opts.ProjectRoot))
                opts.Error ??= "--project-root is required";
            if (string.IsNullOrEmpty(opts.DistRoot))
                opts.Error ??= "--dist-root is required";
        }
        return opts;
    }

    private static void PrintHelp()
    {
        Console.Error.WriteLine("Usage: ReleaseTool --staging <dir> --version <x.y.z> --project-root <dir> --dist-root <dir>");
        Console.Error.WriteLine("       [--source-archive <zip>] [--release-notes <md>] [--user-guide <md>]");
        Console.Error.WriteLine("Output: logs to stdout; final line is MANIFEST_SHA:<64hex> on success.");
    }
}
