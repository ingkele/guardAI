#Requires -Version 5.1
<#
.SYNOPSIS
    guardAI 统一测试脚本——输入项目路径，勾选测试项，一键跑完。
.DESCRIPTION
    测试项：init（装配闸口）/ check（规范检查）/ ui（台账网页，浏览器自动打开，看完回来按回车停止）。
    交互模式：直接运行本脚本（或双击 test.cmd），按提示输入路径、勾选测试项。
    参数模式：.\test.ps1 -Repo "E:\某项目" -Steps init,check
    全部项目都用同一份产物 dist\win-x64\GuardAI.Cli.exe，可用 -Exe 覆盖。
.NOTES
    来源：guardAI v0.3.1 测试驱动脚本（2026-10-21）。脚本内不含固定盘符路径，可整体拷走。
#>
[CmdletBinding()]
param(
    # 要测的项目路径（不填则交互询问）
    [string]$Repo,
    # 测试项：init / check / ui，可多选（不填则交互勾选）
    [string[]]$Steps,
    # 被测程序路径（默认脚本旁 dist\win-x64\GuardAI.Cli.exe）
    [string]$Exe,
    # ui 时不自动打开浏览器
    [switch]$NoBrowser,
    # check 用严格审计模式（存量重复也报违规，案例验收用）
    [switch]$Strict
)

$ErrorActionPreference = 'Stop'

# ---------- 定位被测产物（相对脚本目录，不硬编码盘符） ----------
if (-not $Exe) { $Exe = Join-Path $PSScriptRoot 'dist\win-x64\GuardAI.Cli.exe' }
if (-not (Test-Path $Exe)) {
    Write-Host "[错误] 找不到被测程序: $Exe" -ForegroundColor Red
    Write-Host '请先发版（dotnet publish），或用 -Exe 指定路径。'
    exit 1
}

# ---------- 测试项定义（执行顺序固定 init -> check -> ui） ----------
$stepDefs = [ordered]@{
    init  = '装配闸口（生成 guardai.yml / AGENTS.md 等）'
    check = '规范检查（结构 + 重复，全量扫描）'
    strict = '严格审计（check --strict：存量重复也报——案例验收专用）'
    ui    = '台账网页（浏览器自动打开，看完回来按回车停止）'
}
$stepNames = @($stepDefs.Keys)

# ---------- 帮助方法：从台账读最近一次检查结论（汇总摘要用，不解析控制台文本） ----------
function Get-LastRunNote([string]$RepoPath) {
    $f = Join-Path $RepoPath '.guard\records\runs.jsonl'
    if (-not (Test-Path $f)) { return $null }
    try {
        $last = Get-Content $f -Tail 1 -ErrorAction Stop | ConvertFrom-Json
        $tag = if ($last.outcome -eq 'pass') { '通过 0 违规' } else { "违规 $($last.violationCount) 处" }
        return "$tag，扫描 $($last.filesScanned) 文件"
    }
    catch { return $null }
}

# ---------- 补齐项目路径 ----------
if (-not $Repo) {
    Write-Host ''
    Write-Host '==== guardAI 统一测试 ====' -ForegroundColor Cyan
    $Repo = Read-Host '请输入要测的项目路径'
}
$Repo = $Repo.Trim().Trim('"')
if (-not $Repo -or -not (Test-Path $Repo)) {
    Write-Host "[错误] 路径不存在: $Repo" -ForegroundColor Red
    exit 1
}
$Repo = (Resolve-Path $Repo).Path

# ---------- 补齐测试项 ----------
if (-not $Steps -or $Steps.Count -eq 0) {
    Write-Host ''
    Write-Host '可选测试项（输入编号或名称，逗号分隔；直接回车 = 全部）：' -ForegroundColor Cyan
    for ($i = 0; $i -lt $stepNames.Count; $i++) {
        Write-Host ('  {0}. {1,-6} — {2}' -f ($i + 1), $stepNames[$i], $stepDefs[$stepNames[$i]])
    }
    $sel = (Read-Host '选择').Trim()
    if ($sel -eq '' -or $sel -eq 'all') {
        $Steps = $stepNames
    }
    else {
        $Steps = $sel -split '[,，\s]+' | Where-Object { $_ } | ForEach-Object {
            $t = $_.ToLower()
            if ($t -match '^\d+$') {
                $idx = [int]$t - 1
                if ($idx -ge 0 -and $idx -lt $stepNames.Count) { $stepNames[$idx] } else { $t }
            }
            else { $t }
        }
    }
}

# 规范化：逗号拆分（兼容 -File 模式传 "init,check" 整串）、统一小写、过滤无效项、去重、ui 强制最后执行
$Steps = @($Steps |
    ForEach-Object { $_ -split '[,，\s]+' } |
    Where-Object { $_ } |
    ForEach-Object { $_.ToLower() } |
    Where-Object { $stepDefs.Contains($_) } |
    Select-Object -Unique)
if ($Steps.Count -eq 0) {
    Write-Host '[错误] 没有有效的测试项（可选：init / check / ui）' -ForegroundColor Red
    exit 1
}
$Steps = @($Steps | Where-Object { $_ -ne 'ui' }) + @($Steps | Where-Object { $_ -eq 'ui' })

# ---------- 开跑 ----------
Write-Host ''
Write-Host '==== 开始测试 ====' -ForegroundColor Cyan
Write-Host "项目  : $Repo"
Write-Host "被测  : $Exe"
Write-Host "测试项: $($Steps -join ' -> ')"
Write-Host ''

$results = @()
foreach ($step in $Steps) {
    Write-Host "---- [$step] $($stepDefs[$step]) ----" -ForegroundColor Yellow
    $sw = [System.Diagnostics.Stopwatch]::StartNew()

    if ($step -eq 'ui') {
        # 台账网页是常驻服务：后台启动（浏览器自动打开），回车停止，不卡整体流程
        $uiLog = Join-Path $env:TEMP "guardai-ui-$PID.log"
        $uiArgs = @('ui', '--repo', $Repo)
        if ($NoBrowser) { $uiArgs += '--no-open' }
        $proc = $null
        try {
            $proc = Start-Process -FilePath $Exe -ArgumentList $uiArgs `
                -RedirectStandardOutput $uiLog -RedirectStandardError "$uiLog.err" `
                -PassThru -NoNewWindow
        }
        catch {
            # 部分环境（自动化会话）禁止后台启动：安全跳过，不前台挂死
            $sw.Stop()
            Write-Host '[提示] 当前环境无法后台启动 ui，跳过。' -ForegroundColor Yellow
            Write-Host "可手动跑: $Exe ui --repo `"$Repo`"" -ForegroundColor DarkGray
            $results += [pscustomobject]@{ 项 = 'ui'; 状态 = '⊘ 跳过'; 耗时 = ('{0:n1}s' -f $sw.Elapsed.TotalSeconds); 说明 = '需手动跑（环境限制）' }
            Write-Host ''
            continue
        }

        # 等服务起来抓 URL（最多 5 秒；抓不到不影响使用，浏览器已自动打开）
        $url = $null
        for ($i = 0; $i -lt 25; $i++) {
            Start-Sleep -Milliseconds 200
            if (Test-Path $uiLog) {
                $hit = Get-Content $uiLog -ErrorAction SilentlyContinue |
                    Where-Object { $_ -match 'http://127\.0\.0\.1' } | Select-Object -First 1
                if ($hit) {
                    if ($hit -match 'http://127\.0\.0\.1[:\d/]*') { $url = $Matches[0] }
                    break
                }
            }
            if ($proc.HasExited) { break }
        }
        if ($url) { Write-Host "网页地址: $url" -ForegroundColor Green }
        if ($proc.HasExited) {
            $sw.Stop()
            Write-Host '[警告] 网页服务启动即退出，请查看日志: $uiLog' -ForegroundColor Red
            $results += [pscustomobject]@{ 项 = 'ui'; 状态 = '启动失败'; 耗时 = ('{0:n1}s' -f $sw.Elapsed.TotalSeconds); 说明 = "日志 $uiLog" }
        }
        else {
            Write-Host '浏览器应已自动打开台账网页。看完回来，回到本窗口按回车停止服务…' -ForegroundColor DarkGray
            Read-Host | Out-Null
            $sw.Stop()
            Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
            $results += [pscustomobject]@{ 项 = 'ui'; 状态 = '✔ 通过'; 耗时 = ('{0:n1}s' -f $sw.Elapsed.TotalSeconds); 说明 = '网页已查看并停止' }
        }
        Write-Host ''
        continue
    }

    $stepArgs = switch ($step) {
        # init 自带 [Y/n] 确认，测试场景统一 --yes 一键跳过；init 尾部自带首检
        'init' { @('init', '--repo', $Repo, '--yes') }
        'check' {
            $a = @('check', '--repo', $Repo, '--mode', 'all')
            if ($Strict) { $a += '--strict' }   # 严格审计：基线内存量重复也报
            $a
        }
        'strict' { @('check', '--repo', $Repo, '--mode', 'all', '--strict') }   # 案例验收：存量重复也报
    }
    # 输出直接透传（stdout/stderr 原样到控制台，编码最稳），只记退出码与耗时
    & $Exe @stepArgs
    $code = $LASTEXITCODE
    $sw.Stop()

    $note = Get-LastRunNote $Repo
    # 退出码语义：0 = 通过；1 = 有违规（工具跑通）；2 = 工具出错
    $status = switch ($code) {
        0 { '✔ 通过' }
        1 { '⚠ 有违规' }
        default { "✘ 出错(码 $code)" }
    }
    $results += [pscustomobject]@{ 项 = $step; 状态 = $status; 耗时 = ('{0:n1}s' -f $sw.Elapsed.TotalSeconds); 说明 = $note }
    Write-Host ''
}

# ---------- 汇总 ----------
Write-Host '==== 测试汇总 ====' -ForegroundColor Cyan
$results | Format-Table -AutoSize | Out-String | Write-Host
Write-Host "台账记录: $Repo\.guard\records\runs.jsonl（可跑 guardAI ui 网页查看）" -ForegroundColor DarkGray
