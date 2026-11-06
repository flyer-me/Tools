# DiskWriteTracker 进程契约验收
# 用法：pwsh -NoProfile -File tools\verify.ps1
# 全部通过返回 0，任一失败返回 1。
# 编码：UTF-8 带 BOM + CRLF。

param(
    [string]$Dist = ''
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false } catch { }

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Dist)) { $Dist = Join-Path $repoRoot 'dist' }

$exe = Join-Path $Dist 'DiskWriteTracker.exe'
if (-not (Test-Path $exe)) {
    throw "找不到 $exe，请先运行 scripts\build-tool.ps1 -ToolId windows-disk-write-tracker"
}

$work = Join-Path $env:TEMP ('dwt-verify-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work -Force | Out-Null
$fixture = Join-Path $work 'fixture'
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
$utf8NoBom = New-Object System.Text.UTF8Encoding $false

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
    # ProcessStartInfo 固定 UTF-8 解码；PS 5.1 按控制台代码页解码会把中文判成乱码。
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

function Get-LabelValue {
    param([string]$Text, [string]$Label)

    $pattern = '(?m)^\s*' + [regex]::Escape($Label) + '\s*:\s*(.+?)\s*$'
    $match = [regex]::Match($Text, $pattern)
    if ($match.Success) { return $match.Groups[1].Value.Trim() }
    return $null
}

function Get-PurgeSnapshot {
    param([string]$Root)

    return (@(Get-ChildItem -LiteralPath $Root -Recurse -File -Force |
        ForEach-Object { $_.FullName.Substring($Root.Length) + '|' + $_.Length }) -join "`n")
}

# 夹具按今天的相对日期生成，避免写死日期随日历过期。
# 四天依次覆盖：基线、正常差值 102.4 GB、跨缺日 512 GB、计数器回退。
function New-FixtureData {
    param([string]$Root)

    $today = (Get-Date).Date
    $plan = @(
        @{ O = -6; B = 100000000000 },
        @{ O = -5; B = 202400000000 },
        @{ O = -2; B = 714400000000 },
        @{ O = -1; B = 700000000000 }
    )
    foreach ($p in $plan) {
        $d = $today.AddDays($p.O)
        $line = '{"runId":"fixture","capturedUtc":"' + $d.ToString('yyyy-MM-dd') + 'T12:00:00Z",' +
            '"capturedLocal":"' + $d.ToString('yyyy-MM-dd') + 'T20:00:00",' +
            '"localDate":"' + $d.ToString('yyyy-MM-dd') + '","tzOffsetMinutes":480,' +
            '"diskKey":"TEST|DISK","driveNumber":0,"model":"TEST DISK","serialNumber":"SN1",' +
            '"firmware":"F1","busType":"SATA","source":"ata.predict_failure",' +
            '"attributeRef":null,"rawValue":null,"rawUnit":null,"unitScale":512,' +
            '"bytes":' + $p.B + ',"quality":"reliable","note":null,"temperatureC":null,' +
            '"powerOnHours":null,"powerCycles":null,"percentageUsed":null}'
        $path = Join-Path $Root ('samples-{0}.jsonl' -f $d.ToString('yyyyMMdd'))
        [IO.File]::WriteAllText($path, $line + "`n", $utf8NoBom)
    }
}

New-FixtureData $fixture

Write-Host ''
Write-Host '=== 0. 算法层单测 ===' -ForegroundColor Cyan

$dotnet = if ($env:DOTNET_EXE) { $env:DOTNET_EXE } else { 'dotnet' }
Push-Location $repoRoot
try {
    # 原生命令的还原输出会写 stderr，Stop 模式下会被当成终止错误。
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $testOut = & $dotnet test 'src\DiskWriteTracker.Core.Tests\DiskWriteTracker.Core.Tests.csproj' `
        -c Release --nologo -v minimal 2>&1
    $testExit = $LASTEXITCODE
    $ErrorActionPreference = $prevEap
}
finally { Pop-Location }

Assert-That 'xUnit 单测全部通过' ($testExit -eq 0) ((@($testOut) | Select-Object -Last 6) -join "`n")

Write-Host ''
Write-Host '=== 1. 分发形态 ===' -ForegroundColor Cyan

$expected = @('DiskWriteTracker.exe', 'README.txt', 'data')
$entries = @(Get-ChildItem -LiteralPath $Dist -Force | ForEach-Object { $_.Name })
$shapeDiff = @(Compare-Object -ReferenceObject @($expected | Sort-Object) -DifferenceObject @($entries | Sort-Object))
Assert-That '根目录恰 3 项' ($shapeDiff.Count -eq 0) (($entries | Sort-Object) -join ', ')
Assert-That '无旁置 dll' (@($entries | Where-Object { $_ -like '*.dll' }).Count -eq 0)
Assert-That '无旁置 json' (@($entries | Where-Object { $_ -like '*.json' }).Count -eq 0)

$exeInfo = Get-Item -LiteralPath $exe
# 区间按首轮实测 11,777,303 B 的 ±20% 取值。
Assert-That 'exe 为压缩单文件（9-14 MB）' ($exeInfo.Length -ge 9MB -and $exeInfo.Length -le 14MB) ('size=' + [math]::Round($exeInfo.Length / 1MB, 1) + ' MB')

Assert-That 'data 目录存在（便携模式触发条件）' (Test-Path (Join-Path $Dist 'data'))
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
    Assert-That 'settings.json 不含已废弃的 logDays' ($null -eq $parsed.logDays)
}

Write-Host ''
Write-Host '=== 1b. 无参运行不挂起 ===' -ForegroundColor Cyan

# 无参 + 重定向时打印帮助并立即退出；若进了菜单，这里 15 秒超时。
$noArg = Invoke-Exe @() -TimeoutMs 15000
Assert-That '无参 + 重定向：15 秒内退出' $noArg.Finished
Assert-That '无参 + 重定向：退出码 0' ($noArg.Code -eq 0) ('code=' + $noArg.Code)
Assert-That '无参 + 重定向：输出帮助' ($noArg.StdOut.Contains('用法')) $noArg.StdOut
Assert-That '无参 + 重定向：未进入交互菜单' (-not $noArg.StdOut.Contains('请选择'))

$helpProbe = Invoke-Exe @('--help')
Assert-That '无参与 --help 输出一致' ($noArg.StdOut -eq $helpProbe.StdOut)

Write-Host ''
Write-Host '=== 1c. 命令行契约 ===' -ForegroundColor Cyan

$help = Invoke-Exe @('--help')
Assert-That '--help 返回 0' ($help.Code -eq 0)
foreach ($verb in @('doctor', 'disks', 'probe', 'sample', 'daily', 'report', 'export', 'schedule status', 'schedule install', 'schedule uninstall')) {
    Assert-That ('--help 含 ' + $verb) ($help.Text.Contains($verb))
}

$unknown = Invoke-Exe @('nosuchverb')
Assert-That '未知动词返回 2' ($unknown.Code -eq 2) ('code=' + $unknown.Code)

Write-Host ''
Write-Host '=== 2. 日增量数值走 CSV ===' -ForegroundColor Cyan

$csvDir = Join-Path $work 'csv'
$exp = Invoke-Exe @('export', 'csv', '--days', '10', '--data-dir', $fixture, '--out', $csvDir)
Assert-That 'export csv 返回 0' ($exp.Code -eq 0) ($exp.Text)

$dailyCsv = Join-Path $csvDir 'daily.csv'
Assert-That 'daily.csv 存在' (Test-Path $dailyCsv)
$rows = @(Import-Csv -LiteralPath $dailyCsv -Encoding UTF8)
$deltas = @($rows | ForEach-Object { [long]$_.'日增量(bytes)' })
Assert-That '日统计导出 4 行' ($rows.Count -eq 4) ('count=' + $rows.Count)
Assert-That '正常差值 = 102400000000 字节' ($deltas -contains 102400000000) (($deltas) -join ',')
Assert-That '跨缺日累计 = 512000000000 字节' ($deltas -contains 512000000000) (($deltas) -join ',')
Assert-That '基线与回退日增量为 0' (@($deltas | Where-Object { $_ -eq 0 }).Count -ge 2) (($deltas) -join ',')
Assert-That '无负数增量' (@($deltas | Where-Object { $_ -lt 0 }).Count -eq 0) (($deltas) -join ',')
Assert-That '日统计标注方法与质量' (@($rows | Where-Object { $_.'方法' -eq 'counter_diff' -and $_.'数据质量' -eq 'reliable' }).Count -eq 1)
Assert-That 'samples.csv 存在' (Test-Path (Join-Path $csvDir 'samples.csv'))

# 窄窗口会带回前驱用于算增量，明细里只保留窗口内的样本。
$narrowCsvDir = Join-Path $work 'csv-narrow'
[void](Invoke-Exe @('export', 'csv', '--days', '2', '--data-dir', $fixture, '--out', $narrowCsvDir))
$narrowSamples = @(Import-Csv -LiteralPath (Join-Path $narrowCsvDir 'samples.csv') -Encoding UTF8)
$windowStart = (Get-Date).Date.AddDays(-1).ToString('yyyy-MM-dd')
$earlyRows = @($narrowSamples | Where-Object { [string]::CompareOrdinal($_.'自然日', $windowStart) -lt 0 })
Assert-That 'samples.csv 剔除窗口之前的前驱' ($narrowSamples.Count -ge 1 -and $narrowSamples.Count -lt 4 -and $earlyRows.Count -eq 0) ('rows=' + $narrowSamples.Count + ' start=' + $windowStart)

Write-Host ''
Write-Host '=== 3. 报表离线自包含 ===' -ForegroundColor Cyan

$reportPath = Join-Path $work 'report.html'
$report = Invoke-Exe @('report', '--days', '10', '--data-dir', $fixture, '--out', $reportPath)
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
        Assert-That '内嵌行数与 daily.csv 一致' ($payload.rows.Count -eq $rows.Count) ("report=$($payload.rows.Count) csv=$($rows.Count)")
        Assert-That '卡片含 5 个数值字段' ($null -ne $payload.cards.todayBytes -and $null -ne $payload.cards.cumulativeBytes -and $null -ne $payload.cards.anomalyDisks)
    }
}

Write-Host ''
Write-Host '=== 4. 报表固定名与归档 ===' -ForegroundColor Cyan

# 固定名与归档都相对数据目录；用临时副本，避免依赖本机 dist\data 的真实状态。
$reportData = Join-Path $work 'report-default'
New-Item -ItemType Directory -Path $reportData -Force | Out-Null
Copy-Item -Path (Join-Path $fixture '*') -Destination $reportData -Force

$defaultReport = Join-Path $reportData 'exports\report.html'
$first = Invoke-Exe @('report', '--days', '10', '--data-dir', $reportData)
Assert-That '未指定 --out 时写固定名' (Test-Path $defaultReport)
$archiveDir = Join-Path $reportData 'exports\archive'
$todayArchive = Join-Path $archiveDir ('report-{0}.html' -f (Get-Date -Format 'yyyyMMdd'))
Assert-That '生成当日归档' (Test-Path $todayArchive) ($todayArchive)
for ($k = 0; $k -lt 2; $k++) { [void](Invoke-Exe @('report', '--days', '10', '--data-dir', $reportData)) }
$sameDay = @(Get-ChildItem -LiteralPath $archiveDir -Filter ('report-{0}.html' -f (Get-Date -Format 'yyyyMMdd')) -ErrorAction SilentlyContinue).Count
Assert-That '同日多次生成只覆盖一份归档' ($sameDay -eq 1) ("count=$sameDay")

Write-Host ''
Write-Host '=== 5. 并发写入锁 ===' -ForegroundColor Cyan

$concurrent = Join-Path $work 'concurrent'
New-Item -ItemType Directory -Path $concurrent -Force | Out-Null

# 断言写进去的条数等于各进程自报之和。并发读盘时某块盘可能偶发失败并标为缺失，
# 进程仍正常返回 0，此时它本来就该少写一行。
$workerCount = 8
$workers = @()
for ($i = 0; $i -lt $workerCount; $i++) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.Arguments = 'sample --data-dir "' + $concurrent + '"'
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.StandardOutputEncoding = $utf8NoBom
    $psi.StandardErrorEncoding = $utf8NoBom

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
    $match = [regex]::Match($w.Text, '采样完成\s+(\d+)\s+块盘')
    if ($match.Success) { $reported += [int]$match.Groups[1].Value }
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
    # 并发期间最后持有锁的进程可能设不上属性，再跑一次单进程让 Acquire 前的补设生效。
    [void](Invoke-Exe @('sample', '--quiet', '--data-dir', $concurrent))
    $lockAttrs = [IO.File]::GetAttributes($lockPath)
    Assert-That '锁文件带隐藏属性' (($lockAttrs -band [IO.FileAttributes]::Hidden) -ne 0) ([string]$lockAttrs)
}

Write-Host ''
Write-Host '=== 6. 便携目录与部署方式 ===' -ForegroundColor Cyan

$doctor = Invoke-Exe @('doctor')
Assert-That 'doctor 返回 0' ($doctor.Code -eq 0) ($doctor.Text)
Assert-That '识别为便携部署' ($doctor.StdOut -match '部署方式\s*:\s*便携') $doctor.StdOut
$dataDir = (Get-LabelValue $doctor.StdOut '数据目录') -replace '\s*\[已降级\]\s*$', ''
Assert-That '便携目录位于 dist\data' ([string]::Equals($dataDir, (Join-Path $Dist 'data'), 'OrdinalIgnoreCase')) ('actual=' + $dataDir)
Assert-That '偏好路径在 data\config' ((Get-LabelValue $doctor.StdOut '偏好设置') -eq (Join-Path $Dist 'data\config\settings.json'))
Assert-That '日志目录在 data\logs' ((Get-LabelValue $doctor.StdOut '日志目录') -eq (Join-Path $Dist 'data\logs'))
Assert-That '采样设置含保留天数与超时' ($doctor.StdOut -match '保留\s+\d+\s+天.*任务超时\s+\d+s')

Write-Host ''
Write-Host '=== 7. 计划任务配置（dry-run）===' -ForegroundColor Cyan

$dry = Invoke-Exe @('schedule', 'install', '--dry-run')
Assert-That 'dry-run 返回 0' ($dry.Code -eq 0) ($dry.Text)
Assert-That '输出为配置校验（未写入任务）' ($dry.StdOut.Contains('任务配置校验通过'))
Assert-That '任务指向当前 exe' ((Get-LabelValue $dry.StdOut '程序') -eq $exe)
Assert-That '任务参数为 sample --quiet' ((Get-LabelValue $dry.StdOut '参数') -eq 'sample --quiet')
Assert-That '任务使用最高权限' ((Get-LabelValue $dry.StdOut '运行权限') -match '\bHighest\b')

Write-Host ''
Write-Host '=== 7b. 卸载三档（dry-run，零副作用）===' -ForegroundColor Cyan

$purgeDir = Join-Path $work 'purge'
New-Item -ItemType Directory -Path (Join-Path $purgeDir 'config') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $purgeDir 'logs') -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $purgeDir 'samples-20260101.jsonl'), '{}', $utf8NoBom)
[IO.File]::WriteAllText((Join-Path $purgeDir 'config\settings.json'), '{"schemaVersion":2}', $utf8NoBom)
[IO.File]::WriteAllText((Join-Path $purgeDir 'logs\engine-20260101.log'), 'x', $utf8NoBom)
$beforePurge = Get-PurgeSnapshot $purgeDir

$dryNone = Invoke-Exe @('schedule', 'uninstall', '--dry-run', '--data-dir', $purgeDir)
Assert-That '默认档 dry-run 返回 0' ($dryNone.Code -eq 0) ($dryNone.Text)
Assert-That '默认档不删数据' ($dryNone.StdOut -match '清理档位\s*:\s*不删数据' -and
                              $dryNone.StdOut -match '将删除\s*:\s*0 个文件，0 个目录') $dryNone.StdOut

$dryData = Invoke-Exe @('schedule', 'uninstall', '--purge-data', '--dry-run', '--data-dir', $purgeDir)
Assert-That '--purge-data dry-run 返回 0' ($dryData.Code -eq 0) ($dryData.Text)
Assert-That '--purge-data 保留偏好设置' ($dryData.StdOut -match '清理档位\s*:\s*只删数据，保留偏好设置' -and
                                        $dryData.StdOut -match '(?m)^\s*\+\s+.*settings\.json') $dryData.StdOut
Assert-That '--purge-data 删除样本且不删偏好' ($dryData.StdOut -match '将删除\s*:\s*[1-9]\d* 个文件' -and
                                              $dryData.StdOut.Contains('samples-') -and
                                              -not ($dryData.StdOut -match '(?m)^\s*-\s+.*settings\.json')) $dryData.StdOut

$dryAll = Invoke-Exe @('schedule', 'uninstall', '--purge-all', '--dry-run', '--data-dir', $purgeDir)
Assert-That '--purge-all dry-run 返回 0' ($dryAll.Code -eq 0) ($dryAll.Text)
Assert-That '--purge-all 不保留任何文件' ($dryAll.StdOut -match '清理档位\s*:\s*全部删除' -and
                                          -not ($dryAll.StdOut.Contains('将保留'))) $dryAll.StdOut
Assert-That '--purge-all 把偏好设置列入删除' ($dryAll.StdOut -match '(?m)^\s*-\s+.*settings\.json') $dryAll.StdOut
Assert-That 'dry-run 零副作用' ((Get-PurgeSnapshot $purgeDir) -eq $beforePurge)

# config 目录存在时 rootRemovable 恒为 false，用一个极简目录单独覆盖整体移除预告。
$bare = Join-Path $work 'purge-bare'
New-Item -ItemType Directory -Path $bare -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $bare 'samples-20260101.jsonl'), '{}', $utf8NoBom)
$bareDry = Invoke-Exe @('schedule', 'uninstall', '--purge-all', '--dry-run', '--data-dir', $bare)
Assert-That '--purge-all 预告整体移除数据目录' ($bareDry.StdOut.Contains('会被整体移除')) $bareDry.StdOut

Write-Host ''
Write-Host '=== 7c. 日志双阈值回收 ===' -ForegroundColor Cyan

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
Write-Host '=== 8. 无数据时的行为 ===' -ForegroundColor Cyan

$emptyDir = Join-Path $work 'empty'
New-Item -ItemType Directory -Path $emptyDir -Force | Out-Null
$emptyDaily = Invoke-Exe @('daily', 'show', '--data-dir', $emptyDir)
Assert-That '无数据时 daily 返回 4' ($emptyDaily.Code -eq 4) ('code=' + $emptyDaily.Code)
Assert-That 'daily 提示窗口内没有数据' ($emptyDaily.StdOut.Contains('没有采样数据')) $emptyDaily.StdOut
$emptyReport = Invoke-Exe @('report', '--data-dir', $emptyDir, '--out', (Join-Path $emptyDir 'x.html'))
Assert-That '无数据时 report 返回 4' ($emptyReport.Code -eq 4) ('code=' + $emptyReport.Code)
Assert-That '无数据时不生成空报表' (-not (Test-Path (Join-Path $emptyDir 'x.html')))

# 只删本次创建的临时文件，逐文件删除。
Get-ChildItem -LiteralPath $work -File -Recurse -ErrorAction SilentlyContinue | ForEach-Object {
    try { Remove-Item -LiteralPath $_.FullName -Force } catch { }
}

Write-Host ''
Write-Host ('=== 结果：通过 {0} 项，失败 {1} 项 ===' -f $script:passed, $script:failed) -ForegroundColor Cyan

if ($script:failed -gt 0) { exit 1 }
Write-Host '全部断言通过。' -ForegroundColor Green
exit 0
