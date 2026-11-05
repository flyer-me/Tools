# DiskWriteTracker 验收断言
# 用法：powershell -ExecutionPolicy Bypass -File tools\verify.ps1
# 全部通过返回 0，任一失败抛错并返回 1。
# 编码要求：UTF-8 带 BOM，CRLF（PS 5.1 无 BOM 会解析坏中文）。

param(
    [string]$Dist = ''
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false } catch { }

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Dist)) { $Dist = Join-Path $repoRoot 'dist' }

$exe = Join-Path $Dist 'DiskWriteTracker.exe'
if (-not (Test-Path $exe)) { throw "找不到 $exe，请先运行 tools\build.ps1" }

$fixtures = Join-Path $repoRoot 'tests\fixtures\data'

# 窗口天数从夹具文件名反推，不写死 30。
# 写死的数字会在某一天悄悄落到窗口外，届时全套断言集体变红，而根因只是"今天是几号"。
$fixtureFiles = @(Get-ChildItem -LiteralPath $fixtures -Filter 'samples-*.jsonl' -ErrorAction SilentlyContinue)
if ($fixtureFiles.Count -eq 0) { throw "夹具目录没有样本文件：$fixtures" }

$fixtureDates = @($fixtureFiles | ForEach-Object {
    [datetime]::ParseExact($_.BaseName.Substring(8), 'yyyyMMdd', [Globalization.CultureInfo]::InvariantCulture)
} | Sort-Object)

$fixtureStart = $fixtureDates[0]
$fixtureNewest = $fixtureDates[$fixtureDates.Count - 1]

# 覆盖全部夹具的窗口
$fixtureDays = [int]((Get-Date).Date - $fixtureStart.Date).TotalDays + 1
if ($fixtureDays -lt 1) { $fixtureDays = 1 }

# 只覆盖"最新那一天"的窗口：这一天要靠更早文件里的前驱才能算出增量
$predecessorDays = [int]((Get-Date).Date - $fixtureNewest.Date).TotalDays + 1
if ($predecessorDays -lt 1) { $predecessorDays = 1 }

$newestFileLines = @(Get-Content -LiteralPath (Join-Path $fixtures ('samples-{0}.jsonl' -f $fixtureNewest.ToString('yyyyMMdd'))) -Encoding UTF8 |
    Where-Object { $_.Trim().Length -gt 0 }).Count
$work = Join-Path $env:TEMP ('dwt-verify-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work -Force | Out-Null

$script:passed = 0
$script:failed = 0

function Assert-That {
    param([string]$Name, [bool]$Condition, [string]$Detail = '')

    if ($Condition) {
        $script:passed++
        Write-Host ('  [PASS] ' + $Name) -ForegroundColor Green
    }
    else {
        $script:failed++
        Write-Host ('  [FAIL] ' + $Name) -ForegroundColor Red
        if ($Detail) { Write-Host ('         ' + $Detail) -ForegroundColor DarkGray }
    }
}

function Invoke-Exe {
    # 用 ProcessStartInfo 而不是 `& $exe`：解码编码可控（引擎固定 UTF-8，PS 5.1 默认按
    # 控制台代码页解码，会把中文断言判成乱码），且 EAP='Stop' 下原生命令写 stderr 不抛异常。
    param([string[]]$Argv, [int]$TimeoutMs = 0)

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $utf8 = New-Object System.Text.UTF8Encoding $false
    $psi.StandardOutputEncoding = $utf8
    $psi.StandardErrorEncoding = $utf8

    $quoted = @()
    foreach ($a in $Argv) {
        if ($a -match '[\s"]') { $quoted += '"' + $a.Replace('"', '\"') + '"' } else { $quoted += $a }
    }
    if ($quoted.Count -gt 0) { $psi.Arguments = ($quoted -join ' ') }

    $proc = [System.Diagnostics.Process]::Start($psi)
    $proc.StandardInput.Close()
    $outTask = $proc.StandardOutput.ReadToEndAsync()
    $errTask = $proc.StandardError.ReadToEndAsync()

    $finished = $true
    if ($TimeoutMs -gt 0) {
        $finished = $proc.WaitForExit($TimeoutMs)
        if (-not $finished) { try { $proc.Kill() } catch { } }
    }
    else {
        $proc.WaitForExit()
    }

    $stdout = ''
    $stderr = ''
    $code = -1
    if ($finished) {
        $stdout = $outTask.GetAwaiter().GetResult()
        $stderr = $errTask.GetAwaiter().GetResult()
        $code = $proc.ExitCode
    }
    $proc.Dispose()

    $text = $stdout
    if ($stderr) { $text = $text + "`n" + $stderr }

    return @{ Code = $code; Text = $text; StdOut = $stdout; StdErr = $stderr; Finished = $finished }
}

Write-Host ''
Write-Host '=== 1. 分发形态 ===' -ForegroundColor Cyan

$required = @('DiskWriteTracker.exe', 'README.txt', 'data')
$entries = @(Get-ChildItem -LiteralPath $Dist -Force | ForEach-Object { $_.Name })
$missingEntry = @($required | Where-Object { $entries -notcontains $_ })
Assert-That '根目录三项齐全' ($missingEntry.Count -eq 0) ('缺少: ' + ($missingEntry -join ', '))

# 多文件发布形态：exe + 旁置运行时 dll + runtimeconfig.json，扩展名白名单之外都算异常
$allowedExt = @('.exe', '.dll', '.json', '.txt')
$extra = @($entries | Where-Object {
    $name = $_
    if ($required -contains $name) { return $false }
    $ext = [IO.Path]::GetExtension($name).ToLowerInvariant()
    return $allowedExt -notcontains $ext
})
Assert-That '根目录只有运行时产物（exe/dll/json/txt）' ($extra.Count -eq 0) ('多余: ' + ($extra -join ', '))

$junk = @(Get-ChildItem -LiteralPath $Dist -Recurse -Force -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Extension -in @('.ps1', '.cmd', '.bat', '.psm1') -or $_.Name -like '*.schema.json' })
Assert-That '产物内无脚本或 schema 残留' ($junk.Count -eq 0) (($junk | ForEach-Object { $_.Name }) -join ',')

# 产物内不允许携带 PDB（发布参数 DebugType=none）
$pdb = @(Get-ChildItem -LiteralPath $Dist -Recurse -Force -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Extension -eq '.pdb' })
Assert-That '产物内无 PDB' ($pdb.Count -eq 0) (($pdb | ForEach-Object { $_.Name }) -join ',')

$exeInfo = Get-Item -LiteralPath $exe
Assert-That 'exe 为托管入口壳（< 5 MB，依赖旁置）' ($exeInfo.Length -lt 5MB) ('size=' + [math]::Round($exeInfo.Length / 1MB, 1) + ' MB')

$sideDlls = @(Get-ChildItem -LiteralPath $Dist -File -Force -ErrorAction SilentlyContinue |
    Where-Object { $_.Extension -eq '.dll' })
Assert-That 'exe 旁有自包含运行时 dll' ($sideDlls.Count -gt 0) ('count=' + $sideDlls.Count)

Assert-That 'config 目录已消失' (-not (Test-Path (Join-Path $Dist 'config')))

# data\ 是便携模式的触发条件，必须有。
# settings.json 只在两种情况下存在：从旧版 config\launcher.json 或 data 根迁移过，或用户用过菜单。
# 干净构建时它本就不该出现，所以这里只在存在时校验内容，不强制要求存在。
Assert-That 'data 目录存在（便携模式触发条件）' (Test-Path (Join-Path $Dist 'data'))

# 偏好与样本分属两层：purge 才能用"删数据、留配置"一条规则表达
Assert-That '偏好设置不在数据目录根' (-not (Test-Path (Join-Path $Dist 'data\settings.json')))

$settingsPath = Join-Path $Dist 'data\config\settings.json'
if (Test-Path $settingsPath) {
    $settingsOk = $false
    try {
        $parsed = [IO.File]::ReadAllText($settingsPath, [Text.Encoding]::UTF8) | ConvertFrom-Json
        $settingsOk = ($parsed.schemaVersion -ge 2 -and $null -ne $parsed.reportDays)
    }
    catch { }
    Assert-That 'settings.json 可解析且 schemaVersion >= 2' $settingsOk
    $rawSettings = [IO.File]::ReadAllText($settingsPath, [Text.Encoding]::UTF8) | ConvertFrom-Json
    Assert-That 'settings.json 不再含已废弃的 logDays' ($null -eq $rawSettings.logDays)
}

Write-Host ''
Write-Host '=== 1b. 无参运行不挂起 ===' -ForegroundColor Cyan

# 无参 + 输入/输出重定向时必须打印帮助并立即退出。若进了菜单，这里 15 秒超时。
$noArg = Invoke-Exe @() -TimeoutMs 15000
Assert-That '无参 + 重定向：15 秒内退出（未挂起）' $noArg.Finished
Assert-That '无参 + 重定向：退出码 0' ($noArg.Code -eq 0) ('code=' + $noArg.Code)
Assert-That '无参 + 重定向：输出帮助' ($noArg.StdOut.Contains('用法')) $noArg.StdOut
Assert-That '无参 + 重定向：未进入交互菜单' (-not $noArg.StdOut.Contains('请选择'))

# 帮助文本只有一份来源：无参与 --help 输出应当一致
$helpProbe = Invoke-Exe @('--help')
Assert-That '无参与 --help 输出一致' ($noArg.StdOut -eq $helpProbe.StdOut)

Write-Host ''
Write-Host '=== 1c. 命令行契约未变 ===' -ForegroundColor Cyan

$help = Invoke-Exe @('--help')
Assert-That '--help 返回 0' ($help.Code -eq 0)
foreach ($verb in @('doctor', 'disks', 'probe', 'sample', 'daily', 'report', 'export', 'schedule status', 'schedule install', 'schedule uninstall')) {
    Assert-That ('--help 含 ' + $verb) ($help.Text.Contains($verb))
}

$unknown = Invoke-Exe @('nosuchverb')
Assert-That '未知动词仍返回 2' ($unknown.Code -eq 2) ('code=' + $unknown.Code)

Write-Host ''
Write-Host '=== 2. 日增量四种边界 ===' -ForegroundColor Cyan

$daily = Invoke-Exe @('daily', 'show', '--days', $fixtureDays, '--json', '--data-dir', $fixtures)
Assert-That 'daily show 返回 0' ($daily.Code -eq 0) ($daily.Text)

$stats = ($daily.Text | ConvertFrom-Json).data.stats
$baseline = @($stats | Where-Object { $_.Method -eq 'baseline' })
$diff = @($stats | Where-Object { $_.Method -eq 'counter_diff' })
$gap = @($stats | Where-Object { $_.Method -eq 'gap_filled' })
$clamped = @($stats | Where-Object { $_.Method -eq 'clamped' })

Assert-That '存在基线日' ($baseline.Count -ge 1)
Assert-That '基线的增量为 0' (@($baseline | Where-Object { $_.dailyBytes -ne 0 }).Count -eq 0)
Assert-That '存在正常差值日' ($diff.Count -ge 1)
Assert-That '正常差值 = 102.4 GB' (@($diff | Where-Object { $_.dailyBytes -eq 102400000000 }).Count -ge 1) (($diff | ForEach-Object { $_.dailyBytes }) -join ',')
Assert-That '存在跨期累计日' ($gap.Count -ge 1)
Assert-That '跨期增量 = 512 GB（1,000,000 × 512000 字节）' (@($gap | Where-Object { $_.dailyBytes -eq 512000000000 }).Count -ge 1)
Assert-That '跨期日标注 gapDays>0' (@($gap | Where-Object { $_.gapDays -gt 0 }).Count -ge 1)
Assert-That '存在计数器回退日' ($clamped.Count -ge 1)
Assert-That '回退日增量恒为 0，不出负数' (@($stats | Where-Object { $_.dailyBytes -lt 0 }).Count -eq 0)
Assert-That '回退日质量为 anomaly' (@($clamped | Where-Object { $_.quality -ne 'anomaly' }).Count -eq 0)

Write-Host ''
Write-Host '=== 2b. 窗口读取不丢前驱 ===' -ForegroundColor Cyan

# 只读"最新那一天"的窗口。没有前驱回读的话，这一天会退化成"首次采样，仅建立基线"，
# 增量凭空变成 0 —— 这是把全量读取改成窗口读取最容易引入的静默错误。
$narrow = Invoke-Exe @('daily', 'show', '--days', $predecessorDays, '--json', '--data-dir', $fixtures)
Assert-That '窄窗口 daily 返回 0' ($narrow.Code -eq 0) ($narrow.Text)
$narrowStats = @(($narrow.Text | ConvertFrom-Json).data.stats)
Assert-That '窄窗口仍有数据' ($narrowStats.Count -ge 1) ('count=' + $narrowStats.Count)
Assert-That '窄窗口里没有基线日（说明前驱已补上）' (@($narrowStats | Where-Object { $_.Method -eq 'baseline' }).Count -eq 0) (($narrowStats | ForEach-Object { $_.Method }) -join ',')
Assert-That '窄窗口保留了 512 GB 的跨期增量' (@($narrowStats | Where-Object { $_.dailyBytes -eq 512000000000 }).Count -ge 1) (($narrowStats | ForEach-Object { $_.dailyBytes }) -join ',')
Assert-That '窄窗口保留了计数器回退判定' (@($narrowStats | Where-Object { $_.Quality -eq 'anomaly' }).Count -ge 1)

# 宽窗口与窄窗口在重叠日期上必须逐字段一致：窗口只决定"读几个文件"，不该影响算出来的数
$wideByKey = @{}
foreach ($s in $stats) { $wideByKey[$s.LocalDate + '|' + $s.Model] = $s.dailyBytes }
$drift = 0
foreach ($s in $narrowStats) {
    $key = $s.LocalDate + '|' + $s.Model
    if ($wideByKey.ContainsKey($key) -and $wideByKey[$key] -ne $s.dailyBytes) { $drift++ }
}
Assert-That '宽窄窗口在重叠日期上增量一致' ($drift -eq 0) ('drift=' + $drift)

# 窗口完全落在数据之外时必须老实返回 0 条，而不是把前驱当数据端上来
$outside = Invoke-Exe @('daily', 'show', '--days', '1', '--json', '--data-dir', $fixtures)
Assert-That '窗口内无数据时 daily 返回 4' ($outside.Code -eq 4) ('code=' + $outside.Code)

Write-Host ''
Write-Host '=== 3. 报表离线自包含 ===' -ForegroundColor Cyan

$reportPath = Join-Path $work 'report.html'
$report = Invoke-Exe @('report', '--days', $fixtureDays, '--data-dir', $fixtures, '--out', $reportPath)
Assert-That 'report 返回 0' ($report.Code -eq 0) ($report.Text)
Assert-That '报表文件存在' (Test-Path $reportPath)

$html = [IO.File]::ReadAllText($reportPath, [Text.Encoding]::UTF8)

$externalTokens = @('http://', 'https://', '<link', '<img', '<iframe', '<object', '<embed', '@import', 'src=', 'fetch(', 'XMLHttpRequest', '<script src')
foreach ($token in $externalTokens) {
    Assert-That ('无外部引用: ' + $token) (-not $html.Contains($token))
}

Assert-That '含内嵌数据块 id="dwt-data"' ($html.Contains('id="dwt-data"'))
Assert-That '含 <noscript> 降级视图' ($html.Contains('<noscript>'))
Assert-That '含 5 张汇总卡片（静态视图）' (([regex]::Matches($html, 'class="card"')).Count -eq 5)
Assert-That '数据 JSON 未包含裸 </script>' (-not ($html -match '"</script>'))

$jsonMatch = [regex]::Match($html, '(?s)<script id="dwt-data" type="application/json">(.*?)</script>')
Assert-That '数据块可提取' ($jsonMatch.Success)
if ($jsonMatch.Success) {
    $payload = $null
    try { $payload = $jsonMatch.Groups[1].Value | ConvertFrom-Json } catch { }
    Assert-That '内嵌 JSON 可解析' ($null -ne $payload)
    if ($null -ne $payload) {
        Assert-That '内嵌行数与 daily 一致' ($payload.rows.Count -eq $stats.Count) ("report=$($payload.rows.Count) daily=$($stats.Count)")
        Assert-That '卡片含 5 个数值字段' ($null -ne $payload.cards.todayBytes -and $null -ne $payload.cards.cumulativeBytes -and $null -ne $payload.cards.anomalyDisks)
    }
}

Write-Host ''
Write-Host '=== 4. 报表固定名与归档 ===' -ForegroundColor Cyan

$defaultReport = Join-Path $Dist 'data\exports\report.html'
$first = Invoke-Exe @('report', '--days', $fixtureDays)
Assert-That '未指定 --out 时写固定名' (Test-Path $defaultReport)
$archiveDir = Join-Path $Dist 'data\exports\archive'
$todayArchive = Join-Path $archiveDir ('report-{0}.html' -f (Get-Date -Format 'yyyyMMdd'))
Assert-That '生成当日归档' (Test-Path $todayArchive) ($todayArchive)
for ($k = 0; $k -lt 2; $k++) { [void](Invoke-Exe @('report', '--days', $fixtureDays)) }
$sameDay = @(Get-ChildItem -LiteralPath $archiveDir -Filter ('report-{0}.html' -f (Get-Date -Format 'yyyyMMdd')) -ErrorAction SilentlyContinue).Count
Assert-That '同日多次生成只覆盖一份归档' ($sameDay -eq 1) ("count=$sameDay")

Write-Host ''
Write-Host '=== 5. CSV 编码 ===' -ForegroundColor Cyan

$csvDir = Join-Path $work 'csv'
[void](Invoke-Exe @('export', 'csv', '--days', $fixtureDays, '--data-dir', $fixtures, '--out', $csvDir))
$dailyCsv = Join-Path $csvDir 'daily.csv'
Assert-That 'daily.csv 存在' (Test-Path $dailyCsv)
if (Test-Path $dailyCsv) {
    $csvBytes = [IO.File]::ReadAllBytes($dailyCsv)
    Assert-That 'CSV 带 UTF-8 BOM' ($csvBytes[0] -eq 0xEF -and $csvBytes[1] -eq 0xBB -and $csvBytes[2] -eq 0xBF)
    $csvText = [IO.File]::ReadAllText($dailyCsv, [Text.Encoding]::UTF8)
    Assert-That 'CSV 使用 CRLF' ($csvText.Contains("`r`n"))
}

$samplesCsv = Join-Path $csvDir 'samples.csv'
Assert-That 'samples.csv 存在' (Test-Path $samplesCsv)

# 窄窗口导出：窗口读取会带回起点之前的前驱，但明细里不该出现它们
$narrowCsvDir = Join-Path $work 'csv-narrow'
[void](Invoke-Exe @('export', 'csv', '--days', $predecessorDays, '--data-dir', $fixtures, '--out', $narrowCsvDir))
$narrowRows = @(Get-Content -LiteralPath (Join-Path $narrowCsvDir 'samples.csv') -Encoding UTF8 |
    Select-Object -Skip 1 | Where-Object { $_.Trim().Length -gt 0 })
Assert-That 'samples.csv 剔除窗口之前的前驱' ($narrowRows.Count -eq $newestFileLines) ('rows=' + $narrowRows.Count + ' newest=' + $newestFileLines)

Write-Host ''
Write-Host '=== 6. 并发写入锁 ===' -ForegroundColor Cyan

$concurrent = Join-Path $work 'concurrent'
New-Item -ItemType Directory -Path $concurrent -Force | Out-Null

$diskInfo = Invoke-Exe @('disks', '--json')
$diskCount = ($diskInfo.Text | ConvertFrom-Json).data.count
Assert-That '枚举到物理磁盘' ($diskCount -ge 1) ("count=$diskCount")

# 断言「写进去的条数 == 各进程自报条数」。
# 不能断言「盘数 × 进程数」：并发读盘时某块盘可能偶发失败并被标为缺失
# （设计上不推算、不填补），进程仍然正常返回 0，此时它本来就该少写一行。
$workerCount = 8
$utf8 = New-Object System.Text.UTF8Encoding $false
$workers = @()
for ($i = 0; $i -lt $workerCount; $i++) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.Arguments = 'sample --json --data-dir "' + $concurrent + '"'
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.StandardOutputEncoding = $utf8
    $psi.StandardErrorEncoding = $utf8

    $w = @{ Proc = [System.Diagnostics.Process]::Start($psi) }
    $w.Proc.StandardInput.Close()
    $w.OutTask = $w.Proc.StandardOutput.ReadToEndAsync()
    $w.ErrTask = $w.Proc.StandardError.ReadToEndAsync()
    $workers += $w
}

foreach ($w in $workers) { $w.Proc.WaitForExit() }

$reported = 0
$okWorkers = 0
$badCodes = @()
foreach ($w in $workers) {
    $w.Code = $w.Proc.ExitCode
    $w.Text = $w.OutTask.GetAwaiter().GetResult()
    $w.Err = $w.ErrTask.GetAwaiter().GetResult()
    $w.Proc.Dispose()

    if ($w.Code -ne 0) { $badCodes += [string]$w.Code; continue }
    $okWorkers++
    try { $reported += [int](($w.Text | ConvertFrom-Json).data.count) } catch { }
}

Assert-That ($workerCount.ToString() + ' 个并发进程全部正常退出') ($okWorkers -eq $workerCount) ("ok=$okWorkers code=" + ($badCodes -join ','))

$sampleFile = Get-ChildItem -LiteralPath $concurrent -Filter 'samples-*.jsonl' | Select-Object -First 1
Assert-That '并发采样产生了样本文件' ($null -ne $sampleFile)
if ($null -ne $sampleFile) {
    $lines = @(Get-Content -LiteralPath $sampleFile.FullName -Encoding UTF8 | Where-Object { $_.Trim().Length -gt 0 })
    Assert-That ('写入条数等于各进程自报之和（期望 ' + $reported + ' 条）') ($lines.Count -eq $reported) ("actual=" + $lines.Count)
    Assert-That '每个进程至少写入一条' ($lines.Count -ge $okWorkers) ("lines=" + $lines.Count + ' workers=' + $okWorkers)

    $badLines = 0
    foreach ($line in $lines) {
        try { $null = $line | ConvertFrom-Json } catch { $badLines++ }
    }
    Assert-That '无截断或交错行' ($badLines -eq 0) ("bad=$badLines")

    $perDisk = @($lines | ForEach-Object { ($_ | ConvertFrom-Json).diskKey } | Group-Object)
    Assert-That '每块实际读到的盘都有独立身份' ($perDisk.Count -ge 1) (($perDisk | ForEach-Object { $_.Name + '=' + $_.Count }) -join ' ')
}
$lockPath = Join-Path $concurrent '.write.lock'
Assert-That '锁文件存在且常驻' (Test-Path $lockPath)
if (Test-Path $lockPath) {
    # 并发期间最后持有锁的进程可能设不上属性，再跑一次单进程让 Acquire 前的补设生效
    [void](Invoke-Exe @('sample', '--quiet', '--data-dir', $concurrent))
    $lockAttrs = [IO.File]::GetAttributes($lockPath)
    Assert-That '锁文件带隐藏属性（用户看不到）' (($lockAttrs -band [IO.FileAttributes]::Hidden) -ne 0) ([string]$lockAttrs)
}

Write-Host ''
Write-Host '=== 7. 便携目录与部署方式 ===' -ForegroundColor Cyan

$doctor = Invoke-Exe @('doctor', '--json')
$doctorData = ($doctor.Text | ConvertFrom-Json).data
Assert-That 'doctor 返回 0' ($doctor.Code -eq 0)
Assert-That '识别为便携部署' ($doctorData.dataDirectoryIsPortable -eq $true) ("dir=" + $doctorData.dataDirectory)
Assert-That '便携目录位于 dist\data' ($doctorData.dataDirectory -eq (Join-Path $Dist 'data')) ("actual=" + $doctorData.dataDirectory)
Assert-That 'doctor 暴露生效配置' ($null -ne $doctorData.config.retentionDays -and $null -ne $doctorData.config.timeoutSeconds)
Assert-That 'doctor 报告的偏好路径在 data\config' ($doctorData.settingsPath -eq (Join-Path $Dist 'data\config\settings.json')) ('actual=' + $doctorData.settingsPath)
Assert-That 'doctor 报告的日志目录在 data\logs' ($doctorData.logDirectory -eq (Join-Path $Dist 'data\logs')) ('actual=' + $doctorData.logDirectory)

Write-Host ''
Write-Host '=== 8. 计划任务配置（dry-run，不需要管理员）===' -ForegroundColor Cyan

$dry = Invoke-Exe @('schedule', 'install', '--dry-run', '--json')
Assert-That 'dry-run 返回 0' ($dry.Code -eq 0) ($dry.Text)
if ($dry.Code -eq 0) {
    $planned = $dry.Text | ConvertFrom-Json
    Assert-That '任务指向当前 exe' ($planned.execute -eq $exe) ("actual=" + $planned.execute)
    Assert-That '任务参数为 sample --quiet' ($planned.arguments -eq 'sample --quiet') ("actual=" + $planned.arguments)
    Assert-That '任务使用最高权限' ($planned.runLevel -eq 'Highest')
    Assert-That '启用 StartWhenAvailable' ($planned.startWhenAvailable -eq $true)
}

Write-Host ''
Write-Host '=== 8b. 卸载三档（dry-run，零副作用）===' -ForegroundColor Cyan

$purgeDir = Join-Path $work 'purge'
New-Item -ItemType Directory -Path (Join-Path $purgeDir 'config') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $purgeDir 'logs') -Force | Out-Null
$utf8NoBom = New-Object System.Text.UTF8Encoding $false
[IO.File]::WriteAllText((Join-Path $purgeDir 'samples-20260101.jsonl'), '{}', $utf8NoBom)
[IO.File]::WriteAllText((Join-Path $purgeDir 'config\settings.json'), '{"schemaVersion":2}', $utf8NoBom)
[IO.File]::WriteAllText((Join-Path $purgeDir 'logs\engine-20260101.log'), 'x', $utf8NoBom)
$beforePurge = @(Get-ChildItem -LiteralPath $purgeDir -Recurse -File -Force).Count

$dryNone = Invoke-Exe @('schedule', 'uninstall', '--dry-run', '--data-dir', $purgeDir, '--json')
Assert-That '默认档 dry-run 返回 0' ($dryNone.Code -eq 0) ($dryNone.Text)
$nonePlan = $dryNone.Text | ConvertFrom-Json
Assert-That '默认档不删任何数据' ($nonePlan.purgeMode -eq 'none' -and @($nonePlan.willDeleteFiles).Count -eq 0)

$dryData = Invoke-Exe @('schedule', 'uninstall', '--purge-data', '--dry-run', '--data-dir', $purgeDir, '--json')
Assert-That '--purge-data dry-run 返回 0' ($dryData.Code -eq 0) ($dryData.Text)
$dataPlan = $dryData.Text | ConvertFrom-Json
Assert-That '--purge-data 计划删除样本' (@($dataPlan.willDeleteFiles | Where-Object { $_ -like '*samples-*' }).Count -eq 1) (@($dataPlan.willDeleteFiles) -join ',')
Assert-That '--purge-data 保留偏好设置' (@($dataPlan.willKeep | Where-Object { $_ -like '*settings.json' }).Count -eq 1) (@($dataPlan.willKeep) -join ',')
Assert-That '--purge-data 不把 config 目录列进删除清单' (@($dataPlan.willDeleteDirs | Where-Object { $_ -like '*\config' }).Count -eq 0) (@($dataPlan.willDeleteDirs) -join ',')
Assert-That '--purge-data 不整体删除数据目录' ($dataPlan.rootRemovable -eq $false)

$dryAll = Invoke-Exe @('schedule', 'uninstall', '--purge-all', '--dry-run', '--data-dir', $purgeDir, '--json')
Assert-That '--purge-all dry-run 返回 0' ($dryAll.Code -eq 0) ($dryAll.Text)
$allPlan = $dryAll.Text | ConvertFrom-Json
Assert-That '--purge-all 计划删除偏好设置' (@($allPlan.willDeleteFiles | Where-Object { $_ -like '*settings.json' }).Count -eq 1)
Assert-That '--purge-all 不保留任何文件' (@($allPlan.willKeep).Count -eq 0)

$afterPurge = @(Get-ChildItem -LiteralPath $purgeDir -Recurse -File -Force).Count
Assert-That 'dry-run 一个文件都没删' ($afterPurge -eq $beforePurge) ('before=' + $beforePurge + ' after=' + $afterPurge)

Write-Host ''
Write-Host '=== 8c. 日志双阈值回收 ===' -ForegroundColor Cyan

$logRoot = Join-Path $work 'logret'
New-Item -ItemType Directory -Path (Join-Path $logRoot 'logs') -Force | Out-Null
$stamp = Get-Date
$expiredLog = Join-Path $logRoot ('logs\engine-{0}.log' -f $stamp.AddDays(-90).ToString('yyyyMMdd'))
$todayLog = Join-Path $logRoot ('logs\engine-{0}.log' -f $stamp.ToString('yyyyMMdd'))
[IO.File]::WriteAllText($expiredLog, 'old line', $utf8NoBom)

$sb = New-Object System.Text.StringBuilder
$filler = ('x' * 1023) + "`r`n"
for ($i = 0; $i -lt 1100; $i++) { [void]$sb.Append($filler) }
[IO.File]::WriteAllText($todayLog, $sb.ToString(), $utf8NoBom)
$oversizeBytes = (Get-Item -LiteralPath $todayLog).Length

[void](Invoke-Exe @('sample', '--quiet', '--data-dir', $logRoot))

Assert-That '按天阈值：90 天前的日志被删除' (-not (Test-Path -LiteralPath $expiredLog))
$rolledLog = $todayLog -replace '\.log$', '.1.log'
Assert-That '按体积阈值：超限日志滚动为 .1' (Test-Path -LiteralPath $rolledLog)
if (Test-Path -LiteralPath $rolledLog) {
    Assert-That '滚动后 .1 保留原内容' ((Get-Item -LiteralPath $rolledLog).Length -eq $oversizeBytes) ('size=' + (Get-Item -LiteralPath $rolledLog).Length)
}
Assert-That '滚动后主文件重新变小' ((Get-Item -LiteralPath $todayLog).Length -lt 4096) ('size=' + (Get-Item -LiteralPath $todayLog).Length)

Write-Host ''
Write-Host '=== 9. 无数据时的行为 ===' -ForegroundColor Cyan

$emptyDir = Join-Path $work 'empty'
New-Item -ItemType Directory -Path $emptyDir -Force | Out-Null
$emptyDaily = Invoke-Exe @('daily', 'show', '--data-dir', $emptyDir, '--json')
Assert-That '无数据时 daily 返回 4' ($emptyDaily.Code -eq 4) ("code=" + $emptyDaily.Code)
$emptyReport = Invoke-Exe @('report', '--data-dir', $emptyDir, '--out', (Join-Path $emptyDir 'x.html'))
Assert-That '无数据时 report 返回 4' ($emptyReport.Code -eq 4) ("code=" + $emptyReport.Code)
Assert-That '无数据时不生成空报表' (-not (Test-Path (Join-Path $emptyDir 'x.html')))

# 只删本次创建的临时目录内容，不递归删除整个 TEMP
Get-ChildItem -LiteralPath $work -File -Recurse -ErrorAction SilentlyContinue | ForEach-Object {
    try { Remove-Item -LiteralPath $_.FullName -Force } catch { }
}

Write-Host ''
Write-Host ('=== 结果：通过 {0} 项，失败 {1} 项 ===' -f $script:passed, $script:failed) -ForegroundColor Cyan

if ($script:failed -gt 0) { exit 1 }
Write-Host '全部断言通过。' -ForegroundColor Green
exit 0
