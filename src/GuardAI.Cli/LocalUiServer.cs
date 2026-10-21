using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using GuardAI.Core.Ledging;

namespace GuardAI.Cli;

/// <summary>
/// 本地台账网页服务：guardAI ui 的后端，只绑定 127.0.0.1。
/// </summary>
/// <remarks>
/// 产品铁律：台账有页面（管看）、门禁无头（管拦）。
/// 服务只监听回环地址，页面数据来自本地 .guard/records/，记录不出本机；
/// 页面零外部依赖（无 CDN、无外链），断网可用。
/// </remarks>
internal static class LocalUiServer
{
    /// <summary>默认起始端口（被占用时自动向后找空闲端口）。</summary>
    private const int DefaultPort = 4780;

    /// <summary>端口搜索范围上限（DefaultPort 起共尝试 20 个）。</summary>
    private const int PortSearchRange = 20;

    /// <summary>JSON 响应序列化选项：驼峰命名 + 中文可读。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 启动本地台账网页并阻塞服务（Ctrl+C 退出）。
    /// </summary>
    /// <param name="repoPath">目标仓库根目录。</param>
    /// <param name="port">指定端口；为 null 时从默认端口起找空闲端口。</param>
    /// <param name="openBrowser">是否自动打开浏览器。</param>
    /// <param name="output">控制台输出目标。</param>
    /// <returns>退出码（0 正常退出）。</returns>
    /// <exception cref="ArgumentNullException">repoPath 为 null 时抛出。</exception>
    /// <exception cref="InvalidOperationException">找不到可用端口时抛出。</exception>
    public static int Run(string repoPath, int? port, bool openBrowser, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(repoPath);

        using var listener = StartListener(port, out var boundPort);
        var url = $"http://127.0.0.1:{boundPort}/";

        output.WriteLine("guardAI ui —— 本地台账网页");
        output.WriteLine($"台账: {Ledger.RecordsPath(repoPath)}");
        output.WriteLine($"地址: {url}（仅本机可访问，记录不出本机）");
        output.WriteLine("按 Ctrl+C 退出");

        if (openBrowser)
        {
            TryOpenBrowser(url, output);
        }

        using var stop = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Set();
            listener.Stop();
        };

        try
        {
            while (!stop.IsSet)
            {
                HttpListenerContext context;
                try
                {
                    context = listener.GetContext();
                }
                catch (HttpListenerException)
                {
                    break; // 监听被 Stop() 中断
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                try
                {
                    HandleRequest(context, repoPath);
                }
                catch (IOException)
                {
                    // 浏览器提前断开连接等传输问题不应拖垮服务
                }
            }
        }
        catch (HttpListenerException)
        {
            // 监听关闭竞态，按正常退出处理
        }

        output.WriteLine("台账网页已停止");
        return 0;
    }

    /// <summary>
    /// 启动 HTTP 监听：指定端口被占用时向后找空闲端口。
    /// </summary>
    /// <param name="port">指定端口；null 表示自动找。</param>
    /// <param name="boundPort">实际绑定的端口。</param>
    /// <returns>已启动的监听器。</returns>
    /// <exception cref="InvalidOperationException">指定端口被占用或范围内无空闲端口时抛出。</exception>
    private static HttpListener StartListener(int? port, out int boundPort)
    {
        if (port is { } explicitPort)
        {
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{explicitPort}/");
            try
            {
                listener.Start();
            }
            catch (HttpListenerException ex)
            {
                listener.Close();
                throw new InvalidOperationException($"端口 {explicitPort} 被占用，请换 --port 指定其他端口（{ex.Message}）");
            }

            boundPort = explicitPort;
            return listener;
        }

        for (var candidate = DefaultPort; candidate < DefaultPort + PortSearchRange; candidate++)
        {
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{candidate}/");
            try
            {
                listener.Start();
                boundPort = candidate;
                return listener;
            }
            catch (HttpListenerException)
            {
                listener.Close(); // 端口被占用，试下一个
            }
        }

        throw new InvalidOperationException($"端口 {DefaultPort}~{DefaultPort + PortSearchRange - 1} 均被占用，请用 --port 指定其他端口");
    }

    /// <summary>
    /// 处理单个 HTTP 请求（/ 页面、/api/summary、/api/runs）。
    /// </summary>
    /// <param name="context">HTTP 上下文。</param>
    /// <param name="repoPath">目标仓库根目录。</param>
    private static void HandleRequest(HttpListenerContext context, string repoPath)
    {
        var path = context.Request.Url?.AbsolutePath ?? "/";
        switch (path)
        {
            case "/":
                WriteResponse(context, "text/html; charset=utf-8", BuildPage());
                break;
            case "/api/summary":
                WriteResponse(context, "application/json; charset=utf-8", BuildSummaryJson(repoPath));
                break;
            case "/api/runs":
                WriteResponse(context, "application/json; charset=utf-8", BuildRunsJson(repoPath));
                break;
            default:
                context.Response.StatusCode = 404;
                WriteResponse(context, "text/plain; charset=utf-8", "not found");
                break;
        }
    }

    /// <summary>
    /// 写出 HTTP 响应。
    /// </summary>
    /// <param name="context">HTTP 上下文。</param>
    /// <param name="contentType">响应内容类型。</param>
    /// <param name="body">响应体。</param>
    private static void WriteResponse(HttpListenerContext context, string contentType, string body)
    {
        var buffer = Encoding.UTF8.GetBytes(body);
        context.Response.ContentType = contentType;
        context.Response.ContentLength64 = buffer.Length;
        context.Response.OutputStream.Write(buffer);
        context.Response.OutputStream.Close();
    }

    /// <summary>
    /// 生成 /api/summary 响应（项目元信息 + 闭环汇总）。
    /// </summary>
    /// <param name="repoPath">目标仓库根目录。</param>
    /// <returns>JSON 字符串。</returns>
    private static string BuildSummaryJson(string repoPath)
    {
        var runs = Ledger.Read(repoPath);
        var summary = LedgerAnalyzer.Analyze(runs);
        var payload = new
        {
            Project = Path.GetFileName(Path.TrimEndingDirectorySeparator(repoPath)),
            RepoPath = repoPath,
            RecordsPath = Ledger.RecordsPath(repoPath),
            Summary = summary,
        };
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    /// <summary>
    /// 生成 /api/runs 响应（运行历史，最新在前）。
    /// </summary>
    /// <param name="repoPath">目标仓库根目录。</param>
    /// <returns>JSON 字符串。</returns>
    private static string BuildRunsJson(string repoPath)
    {
        var runs = Ledger.Read(repoPath);
        return JsonSerializer.Serialize(runs.Reverse().ToList(), JsonOptions);
    }

    /// <summary>
    /// 尝试自动打开浏览器（失败不影响服务，用户可手动打开地址）。
    /// </summary>
    /// <param name="url">台账网页地址。</param>
    /// <param name="output">控制台输出目标。</param>
    private static void TryOpenBrowser(string url, TextWriter output)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch (InvalidOperationException)
        {
            output.WriteLine($"自动打开浏览器失败，请手动访问: {url}");
        }
    }

    /// <summary>
    /// 构建台账网页（单页、零外部依赖，页面内再拉取 /api 数据）。
    /// </summary>
    /// <returns>HTML 字符串。</returns>
    private static string BuildPage()
    {
        return PageHtml;
    }

    /// <summary>
    /// 台账网页 HTML（原样常量：内联样式与脚本，无任何外链资源）。
    /// </summary>
    private const string PageHtml = """
        <!DOCTYPE html>
        <html lang="zh-CN">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>guardAI 台账</title>
        <style>
          :root {
            --bg: #f6f7f9; --card: #ffffff; --ink: #1f2937; --muted: #6b7280;
            --line: #e5e7eb; --brand: #2563eb; --ok: #16a34a; --bad: #dc2626; --warn: #d97706;
          }
          * { box-sizing: border-box; }
          body { margin: 0; font-family: "Microsoft YaHei", "PingFang SC", sans-serif; background: var(--bg); color: var(--ink); }
          header { background: var(--card); border-bottom: 1px solid var(--line); padding: 18px 28px; display: flex; align-items: baseline; gap: 14px; flex-wrap: wrap; }
          header h1 { margin: 0; font-size: 20px; }
          header .sub { color: var(--muted); font-size: 13px; }
          main { padding: 24px 28px; max-width: 1200px; margin: 0 auto; }
          .cards { display: grid; grid-template-columns: repeat(auto-fit, minmax(180px, 1fr)); gap: 14px; margin-bottom: 26px; }
          .card { background: var(--card); border: 1px solid var(--line); border-radius: 10px; padding: 16px 18px; }
          .card .label { color: var(--muted); font-size: 13px; }
          .card .value { font-size: 28px; font-weight: 700; margin-top: 6px; }
          .value.bad { color: var(--bad); } .value.ok { color: var(--ok); } .value.brand { color: var(--brand); }
          section { background: var(--card); border: 1px solid var(--line); border-radius: 10px; padding: 18px 20px; margin-bottom: 22px; }
          section h2 { margin: 0 0 14px; font-size: 16px; }
          table { width: 100%; border-collapse: collapse; font-size: 13px; }
          th, td { text-align: left; padding: 8px 10px; border-bottom: 1px solid var(--line); vertical-align: top; word-break: break-all; }
          th { color: var(--muted); font-weight: 600; white-space: nowrap; }
          .tag { display: inline-block; padding: 2px 10px; border-radius: 999px; font-size: 12px; }
          .tag.open { background: #fee2e2; color: var(--bad); }
          .tag.fixed { background: #dcfce7; color: var(--ok); }
          .tag.pass { background: #dcfce7; color: var(--ok); }
          .tag.fail { background: #fee2e2; color: var(--bad); }
          .tag.hook { background: #e0e7ff; color: var(--brand); }
          .tag.ci { background: #fef3c7; color: var(--warn); }
          .tag.cli { background: #e5e7eb; color: var(--muted); }
          .muted { color: var(--muted); }
          .toolbar { display: flex; gap: 10px; align-items: center; margin-bottom: 18px; }
          button { background: var(--brand); color: #fff; border: 0; border-radius: 8px; padding: 8px 16px; font-size: 13px; cursor: pointer; }
          button:hover { opacity: 0.9; }
          .empty { color: var(--muted); padding: 14px 0; }
          footer { color: var(--muted); font-size: 12px; text-align: center; padding: 10px 0 28px; }
        </style>
        </head>
        <body>
        <header>
          <h1>guardAI 台账</h1>
          <span class="sub" id="project">加载中…</span>
          <span class="sub">本地记录 · 不出本机</span>
        </header>
        <main>
          <div class="toolbar">
            <button onclick="load()">刷新</button>
            <span class="muted" id="records"></span>
          </div>
          <div class="cards">
            <div class="card"><div class="label">运行总数</div><div class="value brand" id="c-runs">-</div></div>
            <div class="card"><div class="label">当前未闭环</div><div class="value bad" id="c-open">-</div></div>
            <div class="card"><div class="label">已闭环（指纹消失）</div><div class="value ok" id="c-fixed">-</div></div>
            <div class="card"><div class="label">闭环率</div><div class="value" id="c-rate">-</div></div>
          </div>
          <section>
            <h2>违规指纹追踪</h2>
            <table>
              <thead><tr><th>状态</th><th>规则</th><th>文件</th><th>说明</th><th>出现</th><th>首见</th><th>末见</th></tr></thead>
              <tbody id="fp-rows"></tbody>
            </table>
            <div class="empty" id="fp-empty" style="display:none">暂无违规指纹记录</div>
          </section>
          <section>
            <h2>运行历史（含 AI hook / CI 结论）</h2>
            <table>
              <thead><tr><th>时间</th><th>触发</th><th>模式</th><th>扫描</th><th>违规</th><th>结论</th></tr></thead>
              <tbody id="run-rows"></tbody>
            </table>
            <div class="empty" id="run-empty" style="display:none">暂无运行记录——先执行 guardAI check</div>
          </section>
        </main>
        <footer>guardAI · 门禁无头（管拦），台账有页面（管看）</footer>
        <script>
          const esc = (s) => String(s ?? '').replace(/[&<>"]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c]));
          const fmt = (t) => t ? new Date(t).toLocaleString('zh-CN', { hour12: false }) : '-';
          const pct = (r) => (r * 100).toFixed(1) + '%';

          async function load() {
            const [sRes, rRes] = await Promise.all([fetch('/api/summary'), fetch('/api/runs')]);
            const head = await sRes.json();
            const runs = await rRes.json();
            const s = head.summary;

            document.getElementById('project').textContent = head.project + ' · ' + head.repoPath;
            document.getElementById('records').textContent = '台账文件: ' + head.recordsPath;
            document.getElementById('c-runs').textContent = s.totalRuns;
            document.getElementById('c-open').textContent = s.openCount;
            document.getElementById('c-fixed').textContent = s.fixedCount;
            const rateEl = document.getElementById('c-rate');
            rateEl.textContent = pct(s.closureRate);
            rateEl.className = 'value ' + (s.openCount > 0 ? 'bad' : 'ok');

            const fpRows = s.fingerprints.map(f => `
              <tr>
                <td><span class="tag ${esc(f.status)}">${f.status === 'open' ? '未闭环' : '已闭环'}</span></td>
                <td>${esc(f.rule)}</td>
                <td>${esc(f.file)}</td>
                <td>${esc(f.message)}</td>
                <td>${esc(f.occurrences)} 次</td>
                <td class="muted">${fmt(f.firstSeen)}</td>
                <td class="muted">${fmt(f.lastSeen)}</td>
              </tr>`).join('');
            document.getElementById('fp-rows').innerHTML = fpRows;
            document.getElementById('fp-empty').style.display = s.fingerprints.length ? 'none' : 'block';

            const runRows = runs.map(r => `
              <tr>
                <td class="muted">${fmt(r.timestamp)}</td>
                <td><span class="tag ${esc(r.trigger)}">${esc(r.trigger)}</span></td>
                <td>${esc(r.mode)}</td>
                <td>${esc(r.filesScanned)}</td>
                <td>${esc(r.violationCount)}</td>
                <td><span class="tag ${esc(r.outcome)}">${r.outcome === 'pass' ? '通过' : '违规'}</span></td>
              </tr>`).join('');
            document.getElementById('run-rows').innerHTML = runRows;
            document.getElementById('run-empty').style.display = runs.length ? 'none' : 'block';
          }

          load();
          setInterval(load, 10000);
        </script>
        </body>
        </html>
        """;
}
