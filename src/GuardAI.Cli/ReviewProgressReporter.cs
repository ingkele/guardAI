using GuardAI.AI.Config;
using GuardAI.AI.Review;

namespace GuardAI.Cli;

/// <summary>
/// review 进度输出器（Issue #4）：在模型调用的长等待期间给用户可观察的进度。
/// </summary>
/// <remarks>
/// 设计约束：
/// ①全部写 <see cref="Console.Error"/>——<c>--json</c> 模式 stdout 必须是可解析的纯 JSON，
///   进度混进去会让消费者解析失败；
/// ②心跳用 <see cref="Timer"/> 定时打印而非 Task.Delay 循环，不额外占用线程池线程，
///   也不阻塞主流程（模型返回后立即停表）；
/// ③心跳只打耗时秒数，不打点号堆积——CI 日志里点号会变成噪音，秒数信息量更高。
/// </remarks>
internal sealed class ReviewProgressReporter : IDisposable
{
    /// <summary>心跳间隔（秒）：够短能看出没卡死，够长不刷屏。</summary>
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    private readonly TextWriter _stderr;
    private readonly AIConfig _config;
    private readonly string _mode;
    private readonly DateTime _startedAt = DateTime.UtcNow;
    private Timer? _heartbeat;
    private volatile bool _modelReturned;

    /// <summary>
    /// 构造进度输出器。
    /// </summary>
    /// <param name="stderr">标准错误输出（进度一律走这里）。</param>
    /// <param name="config">AI 配置（回显模型名与超时上限）。</param>
    /// <param name="mode">差异收集模式（changed / staged / base / all），回显用。</param>
    public ReviewProgressReporter(TextWriter stderr, AIConfig config, string mode)
    {
        _stderr = stderr;
        _config = config;
        _mode = mode;
    }

    /// <summary>
    /// 处理一条进度事件。
    /// </summary>
    /// <param name="progress">进度事件。</param>
    public void Report(ReviewProgress progress)
    {
        switch (progress.Stage)
        {
            case ReviewProgressStage.Collected:
                // 送审规模在调用模型之前就告诉用户——原实现等模型返回后才打印，长等待全程黑箱
                _stderr.WriteLine(
                    $"送审: {progress.Files} 个文件 / {progress.Chars} 字符"
                    + $"（mode={_mode}，模型={_config.Model}）");
                break;

            case ReviewProgressStage.CallingModel:
                _stderr.WriteLine(
                    $"模型调用中…（超时上限 {_config.TimeoutSeconds}s，Ctrl+C 可中断）");
                StartHeartbeat();
                break;

            case ReviewProgressStage.ModelReturned:
                StopHeartbeat();
                _modelReturned = true;
                _stderr.WriteLine($"模型已返回（耗时 {(DateTime.UtcNow - _startedAt).TotalSeconds:F0}s），正在解析结果…");
                break;
        }
    }

    /// <summary>
    /// 启动心跳定时器（重复调用无副作用）。
    /// </summary>
    private void StartHeartbeat()
    {
        if (_heartbeat is not null)
        {
            return;
        }

        _heartbeat = new Timer(_ =>
        {
            if (_modelReturned)
            {
                return;
            }

            var elapsed = (int)(DateTime.UtcNow - _startedAt).TotalSeconds;
            _stderr.WriteLine($"…仍在等待模型响应（已 {(elapsed == 0 ? 1 : elapsed)}s）");
        }, null, HeartbeatInterval, HeartbeatInterval);
    }

    /// <summary>停止心跳（幂等）。</summary>
    private void StopHeartbeat()
    {
        _heartbeat?.Dispose();
        _heartbeat = null;
    }

    /// <inheritdoc />
    public void Dispose() => StopHeartbeat();
}