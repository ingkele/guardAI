namespace GuardAI.AI.Review;

/// <summary>
/// review 进度阶段（Issue #4：把「黑箱等待」变成「可观察进度」）。
/// </summary>
public enum ReviewProgressStage
{
    /// <summary>差异收集完成（已知送审规模，模型尚未调用）。</summary>
    Collected,

    /// <summary>正在调用模型（最长的等待段）。</summary>
    CallingModel,

    /// <summary>模型已返回（开始解析与出报告）。</summary>
    ModelReturned,
}

/// <summary>
/// review 单次进度事件。
/// </summary>
/// <param name="Stage">所处阶段。</param>
/// <param name="Files">送审文件数。</param>
/// <param name="Chars">送审字符数（截断后的实际送审长度）。</param>
public sealed record ReviewProgress(ReviewProgressStage Stage, int Files, int Chars);