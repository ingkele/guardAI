namespace GuardAI.Checks.Assets;

/// <summary>
/// C# 源码中抽取出的公共方法快照（能力清单与重复检测共用的成员模型）。
/// </summary>
/// <param name="TypeName">所属类型名（嵌套类型取最内层）。</param>
/// <param name="Name">方法名。</param>
/// <param name="Signature">归一化单行签名（修饰符 + 返回类型 + 方法名 + 参数列表）。</param>
/// <param name="File">仓库相对路径（正斜杠）。</param>
/// <param name="Line">声明起始行号（1 起）。</param>
/// <param name="BodyText">归一化方法体（去注释/字符串字面量/空白），M2 近似检测的原料。</param>
/// <param name="BodyHash">方法体归一化文本的 SHA-256 前 16 位十六进制。</param>
/// <param name="Summary">XML 文档注释摘要（含中文说明，v0.3.1-② 中文检索的原料；无注释为空串）。</param>
public sealed record CodeMember(
    string TypeName,
    string Name,
    string Signature,
    string File,
    int Line,
    string BodyText,
    string BodyHash,
    string Summary = "");
