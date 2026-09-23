using System.Diagnostics;

namespace QL.World.ReleaseTool;

/// <summary>
/// Git 信息提供者：返回当前 HEAD 的 commit SHA、short SHA、当前分支名。
/// 测试通过实现此接口跳过真实 git 调用；默认实现 ProcessGitInfoProvider 启动 git 子进程。
/// </summary>
public interface IGitInfoProvider
{
    string GetCommitSha();
    string GetCommitShortSha();
    string GetBranch();
}

/// <summary>
/// 默认实现：通过 git -C {ProjectRoot} rev-parse ... 启动子进程读取 HEAD 信息。
/// </summary>
public sealed class ProcessGitInfoProvider : IGitInfoProvider
{
    private readonly string _projectRoot;

    public ProcessGitInfoProvider(string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
            throw new ArgumentException("ProjectRoot is required.", nameof(projectRoot));
        _projectRoot = projectRoot;
    }

    public string GetCommitSha()
        => RunGitRequired("rev-parse", "HEAD").Trim();

    public string GetCommitShortSha()
        => RunGitRequired("rev-parse", "--short", "HEAD").Trim();

    public string GetBranch()
        => RunGitRequired("rev-parse", "--abbrev-ref", "HEAD").Trim();

    private string RunGitRequired(params string[] args)
    {
        int code = GitRunner.Run(_projectRoot, args, out string stdout, out string stderr);
        if (code != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} failed (exit {code}): {stderr}");
        }
        return stdout;
    }
}

/// <summary>启动 git 子进程的辅助类，ReleaseArtifacts 与 ProcessGitInfoProvider 共用。</summary>
internal static class GitRunner
{
    public static int Run(string workingDir, IReadOnlyList<string> args, out string stdout, out string stderr)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        stdout = proc.StandardOutput.ReadToEnd();
        stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return proc.ExitCode;
    }
}
