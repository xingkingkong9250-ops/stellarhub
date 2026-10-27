namespace QL.World.Services.Ai;

/// <summary>
/// 0.16.0 DUP-E: 可选能力接口——双图 VLM 对比，用于查重模糊边界带终审。
/// 仅 <see cref="CloudAiService"/> 实现；<see cref="DisabledAiService"/> /
/// 本地后端不实现，扫描服务按 <c>is IVlmImageCompare</c> 探测决定是否执行 L3。
/// </summary>
public interface IVlmImageCompare
{
    /// <summary>
    /// 判断两张图片是否为同一画面的重复副本（允许转码/缩放/调色/轻微裁剪）。
    /// 返回 <c>(SameImage, Confidence)</c>；实现不得抛异常，失败（未配置、
    /// 网络、解析）返回 <c>(false, 0)</c>，调用方把 <c>Confidence &lt;= 0</c>
    /// 视为「无判定」而非「否决」。
    /// </summary>
    Task<(bool SameImage, double Confidence)> CompareImagesAsync(
        string pathA, string pathB, CancellationToken cancellationToken);
}
