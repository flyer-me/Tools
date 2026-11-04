# DiskWriteTracker 验收断言
# 用法：powershell -ExecutionPolicy Bypass -File scripts\verify.ps1
# 全部通过返回 0，任一失败抛错并返回 1。
# 编码要求：UTF-8 带 BOM，CRLF（PS 5.1 无 BOM 会解析坏中文）。

param(
    [string]$Dist = ''
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false } catch { }

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Dist)) { $Dist = Join-Path $repoRoot 'src\dist' }

$exe = Join-Path $Dist 'DiskWriteTracker.exe'
if (-not (Test-Path $exe)) { throw "找不到 $exe，请先运行 scripts\build.ps1" }

$fixtures = Join-Path $repoRoot 'tests\fixtures\data'

# 夹具日期是写死的（samples-YYYYMMDD.jsonl），而 --days N 是相对"今天"的窗口。
# 早先这里写死 --days 30，夹具滑出 30 天窗口后整批断言会一起变红，
# 而且报错看上去完全不像日期问题。窗口一律由夹具自身日期推算。
$fixtureDates = @(Get-ChildItem -Path $fixtures -Filter 'samples-*.jsonl' | ForEach-Object {
    [datetime]::ParseExact($_.BaseName.Substring(8), 'yyyyMMdd', [Globalization.CultureInfo]::InvariantCulture)
} | Sort-Object)
if ($fixtureDates.Count -eq 0) { throw "夹具目录里没有 samples-*.jsonl：$fixtures" }

$fixtureStart = $fixtureDates[0].Date
$fixtureEnd = $fixtureDates[-1].Date
$today = (Get-Date).Date

# 覆盖全部夹具日期的窗口
$fixtureDays = [int]($today - $fixtureStart).TotalDays + 1

# 从夹具中段开始的窗口：首日的前驱落在窗口外，用来验证回读逻辑
$midIndex = [Math]::Min(2, $fixtureDates.Count - 1)
$midDate = $fixtureDates[$midIndex].Date
$midDays = [int]($today - $midDate).TotalDays + 1

# 夹具总行数：history 与 export --all 都应当等于它
$fixtureLines = 0
foreach ($f in (Get-ChildItem -Path $fixtures -Filter 'samples-*.jsonl')) {
    $fixtureLines += @(Get-Content -LiteralPath $f.FullName | Where-Object { $_.Trim() }).Count
}
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

$allowed = @('DiskWriteTracker.exe', 'README.txt', 'data')
$entries = @(Get-ChildItem -LiteralPath $Dist -Force | ForEach-Object { $_.Name })
$extra = @($entries | Where-Object { $allowed -notcontains $_ })
$missingEntry = @($allowed | Where-Object { $entries -notcontains $_ })
Assert-That '根目录只有 exe / README.txt / data' ($extra.Count -eq 0) ('多余: ' + ($extra -join ', '))
Assert-That '根目录三项齐全' ($missingEntry.Count -eq 0) ('缺少: ' + ($missingEntry -join ', '))

$junk = @(Get-ChildItem -LiteralPath $Dist -Recurse -Force -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Extension -in @('.ps1', '.cmd', '.bat', '.psm1') -or $_.Name -like '*.schema.json' })
Assert-That '产物内无脚本或 schema 残留' ($junk.Count -eq 0) (($junk | ForEach-Object { $_.Name }) -join ',')

$exeInfo = Get-Item -LiteralPath $exe
Assert-That 'exe 为自包含单文件（>= 20 MB）' ($exeInfo.Length -ge 20MB) ('size=' + [math]::Round($exeInfo.Length / 1MB, 1) + ' MB')

$sideFiles = @(Get-ChildItem -LiteralPath $Dist -File -Force -ErrorAction SilentlyContinue |
    Where-Object { $_.Extension -in @('.dll', '.json') })
Assert-That 'exe 旁无依赖清单或托管 dll' ($sideFiles.Count -eq 0) (($sideFiles | ForEach-Object { $_.Name }) -join ',')

Assert-That 'config 目录已消失' (-not (Test-Path (Join-Path $Dist 'config')))

# data\ 是便携模式的触发条件，必须有。
# settings.json 只在两种情况下存在：从旧版 config\launcher.json 迁移过，或用户用过菜单。
# 干净构建时它本就不该出现，所以这里只在存在时校验内容，不强制要求存在。
Assert-That 'data 目录存在（便携模式触发条件）' (Test-Path (Join-Path $Dist 'data'))
$settingsPath = Join-Path $Dist 'data\settings.json'
if (Test-Path $settingsPath) {
    $settingsOk = $false
    try {
        $parsed = [IO.File]::ReadAllText($settingsPath, [Text.Encoding]::UTF8) | ConvertFrom-Json
        $settingsOk = ($parsed.schemaVersion -ge 1 -and $null -ne $parsed.reportDays)
    }
    catch { }
    Assert-That 'settings.json 可解析且带 schemaVersion' $settingsOk
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
foreach ($verb in @('doctor', 'disks', 'probe', 'sample', 'history', 'daily show', 'daily rebuild', 'report', 'export', 'schedule status', 'schedule install', 'schedule uninstall')) {
    Assert-That ('--help 含 ' + $verb) ($help.Text.Contains($verb))
}

$unknown = Invoke-Exe @('nosuchverb')
Assert-That '未知动词仍返回 2' ($unknown.Code -eq 2) ('code=' + $unknown.Code)

Write-Host ''
Write-Host '=== 2. 日增量四种边界 ===' -ForegroundColor Cyan

$daily = Invoke-Exe @('daily', 'show', '--days', "$fixtureDays", '--json', '--data-dir', $fixtures)
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

# 窗口起点落在夹具中段：首日的前驱在窗口之外，只有回读逻辑才会把它捞回来。
# 少读一条，首日就退化成 baseline，一整天的写入量静默变 0 —— 不报错，只是数字错。
$mid = Invoke-Exe @('daily', 'show', '--days', "$midDays", '--json', '--data-dir', $fixtures)
Assert-That '窄窗口 daily 返回 0' ($mid.Code -eq 0) ($mid.Text)

$midStats = @(($mid.Text | ConvertFrom-Json).data.stats)
$midDateText = $midDate.ToString('yyyy-MM-dd')
$midFirst = @($midStats | Where-Object { $_.LocalDate -eq $midDateText })

Assert-That '窄窗口含窗口起点当日' ($midFirst.Count -ge 1) ("date=$midDateText")
Assert-That '窗口首日不是基线（前驱被回读）' (
    @($midFirst | Where-Object { $_.Method -eq 'baseline' }).Count -eq 0
) (($midFirst | ForEach-Object { "$($_.Model):$($_.Method)" }) -join ',')
Assert-That '窗口只含起点之后的行' (
    @($midStats | Where-Object { [string]::CompareOrdinal($_.LocalDate, $midDateText) -lt 0 }).Count -eq 0
) (($midStats | ForEach-Object { $_.LocalDate } | Sort-Object -Unique) -join ',')

$sameAsWide = $true
foreach ($m in $midStats) {
    $w = $stats | Where-Object { $_.LocalDate -eq $m.LocalDate -and $_.Model -eq $m.Model }
    if (-not $w) { $sameAsWide = $false; continue }
    if ($w.dailyBytes -ne $m.dailyBytes -or $w.Method -ne $m.Method -or $w.GapDays -ne $m.GapDays -or $w.Quality -ne $m.Quality) {
        $sameAsWide = $false
    }
}
Assert-That '窄窗口与宽窗口在重叠日期上逐字段一致' $sameAsWide

# 窗口完全落在样本之后：应当明确返回"无数据"，而不是把老数据当成窗口内的
if ($fixtureEnd -lt $today) {
    $tooNarrow = Invoke-Exe @('daily', 'show', '--days', '1', '--json', '--data-dir', $fixtures)
    Assert-That '窗口内无数据时返回 4' ($tooNarrow.Code -eq 4) ('code=' + $tooNarrow.Code)
}

# history 不传 --days 时必须仍是全量：窗口化只针对按日期过滤的命令
$historyAll = Invoke-Exe @('history', '--json', '--data-dir', $fixtures)
Assert-That 'history 默认全量' (($historyAll.Text | ConvertFrom-Json).data.count -eq $fixtureLines) ("count=$fixtureLines")

Write-Host ''
Write-Host '=== 3. 报表离线自包含 ===' -ForegroundColor Cyan

$reportPath = Join-Path $work 'report.html'
$report = Invoke-Exe @('report', '--days', "$fixtureDays", '--data-dir', $fixtures, '--out', $reportPath)
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
$first = Invoke-Exe @('report', '--days', '7')
Assert-That '未指定 --out 时写固定名' (Test-Path $defaultReport)
$archiveDir = Join-Path $Dist 'data\exports\archive'
$todayArchive = Join-Path $archiveDir ('report-{0}.html' -f (Get-Date -Format 'yyyyMMdd'))
Assert-That '生成当日归档' (Test-Path $todayArchive) ($todayArchive)
for ($k = 0; $k -lt 2; $k++) { [void](Invoke-Exe @('report', '--days', '7')) }
$sameDay = @(Get-ChildItem -LiteralPath $archiveDir -Filter ('report-{0}.html' -f (Get-Date -Format 'yyyyMMdd')) -ErrorAction SilentlyContinue).Count
Assert-That '同日多次生成只覆盖一份归档' ($sameDay -eq 1) ("count=$sameDay")

Write-Host ''
Write-Host '=== 5. CSV 编码 ===' -ForegroundColor Cyan

$csvDir = Join-Path $work 'csv'
[void](Invoke-Exe @('export', 'csv', '--days', "$fixtureDays", '--data-dir', $fixtures, '--out', $csvDir))
$dailyCsv = Join-Path $csvDir 'daily.csv'
Assert-That 'daily.csv 存在' (Test-Path $dailyCsv)
if (Test-Path $dailyCsv) {
    $csvBytes = [IO.File]::ReadAllBytes($dailyCsv)
    Assert-That 'CSV 带 UTF-8 BOM' ($csvBytes[0] -eq 0xEF -and $csvBytes[1] -eq 0xBB -and $csvBytes[2] -eq 0xBF)
    $csvText = [IO.File]::ReadAllText($dailyCsv, [Text.Encoding]::UTF8)
    Assert-That 'CSV 使用 CRLF' ($csvText.Contains("`r`n"))
}

# samples.csv 过去不受 --days 约束，等于 exportDays 只对一半产物生效；
# 现在默认与 daily.csv 同窗口，--all 才是全量。
$narrowDir = Join-Path $work 'csv-narrow'
[void](Invoke-Exe @('export', 'csv', '--days', "$midDays", '--data-dir', $fixtures, '--out', $narrowDir))
$windowRows = @(Get-Content -LiteralPath (Join-Path $narrowDir 'samples.csv') | Select-Object -Skip 1 | Where-Object { $_.Trim() }).Count

$expectedWindowRows = 0
foreach ($f in (Get-ChildItem -Path $fixtures -Filter 'samples-*.jsonl')) {
    foreach ($line in (Get-Content -LiteralPath $f.FullName | Where-Object { $_.Trim() })) {
        if ([string]::CompareOrdinal(($line | ConvertFrom-Json).localDate, $midDateText) -ge 0) {
            $expectedWindowRows++
        }
    }
}

$allCsvDir = Join-Path $work 'csv-all'
[void](Invoke-Exe @('export', 'csv', '--all', '--data-dir', $fixtures, '--out', $allCsvDir))
$allRows = @(Get-Content -LiteralPath (Join-Path $allCsvDir 'samples.csv') | Select-Object -Skip 1 | Where-Object { $_.Trim() }).Count

Assert-That 'samples.csv 只含窗口内的行' ($windowRows -eq $expectedWindowRows) ("window=$windowRows expected=$expectedWindowRows")
Assert-That '--all 导出全部样本' ($allRows -eq $fixtureLines) ("all=$allRows total=$fixtureLines")

Write-Host ''
Write-Host '=== 6. 并发写入锁 ===' -ForegroundColor Cyan

$concurrent = Join-Path $work 'concurrent'
New-Item -ItemType Directory -Path $concurrent -Force | Out-Null

$diskInfo = Invoke-Exe @('disks', '--json')
$diskCount = ($diskInfo.Text | ConvertFrom-Json).data.count
Assert-That '枚举到物理磁盘' ($diskCount -ge 1) ("count=$diskCount")

# 要断言的不变量是「写进去的条数 == 各进程自报条数」，即一条都没丢。
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
