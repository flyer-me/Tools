<#
.SYNOPSIS
    DiskWriteTracker 计划任务管理。
.DESCRIPTION
    仅使用 ScheduledTasks 模块（本机 schtasks.exe 可能被安全策略禁用）。
    所有动作均幂等：重复 install 不会产生第二个任务。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('install', 'status', 'uninstall', 'run')]
    [string]$Action,

    [string]$ExePath,
    [string]$TaskName = 'DiskWriteTracker_DailySample',
    [string]$Time = '00:05',
    [switch]$PurgeData,
    [switch]$PurgeAll,
    [switch]$DryRun,
    [string]$DataDirectory
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
$TaskPath = '\DiskWriteTracker\'

function Write-Result {
    param([hashtable]$Payload)
    $Payload | ConvertTo-Json -Depth 6 -Compress
}

function Get-ExistingTask {
    Get-ScheduledTask -TaskPath $TaskPath -TaskName $TaskName -ErrorAction SilentlyContinue
}

function Convert-ResultCode {
    param([int]$Code)
    switch ($Code) {
        0 { '成功' }
        267008 { '任务已就绪' }
        267009 { '正在运行' }
        267011 { '尚未运行' }
        267012 { '已终止' }
        2147942401 { '未找到任务' }
        default {
            if ($Code -eq 0) { '成功' } else { "失败码 $Code" }
        }
    }
}

function Build-Task {
    param([string]$RunLevel, [string]$LogonType = 'S4U')

    if (-not $ExePath) { throw '未提供 ExePath' }
    if (-not (Test-Path $ExePath)) { throw "可执行文件不存在: $ExePath" }

    $workDir = Split-Path -Parent $ExePath

    $action = New-ScheduledTaskAction -Execute $ExePath -Argument 'sample --quiet' -WorkingDirectory $workDir
    $trigger = New-ScheduledTaskTrigger -Daily -At $Time
    $settings = New-ScheduledTaskSettingsSet `
        -StartWhenAvailable `
        -MultipleInstances IgnoreNew `
        -ExecutionTimeLimit (New-TimeSpan -Minutes 30) `
        -AllowStartIfOnBatteries `
        -DontStopIfGoingOnBatteries `
        -RestartCount 2 `
        -RestartInterval (New-TimeSpan -Minutes 10)
    $principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType $LogonType -RunLevel $RunLevel

    return New-ScheduledTask -Action $action -Trigger $trigger -Settings $settings -Principal $principal
}

function Add-TreeItems {
    <# 把 $Path 下的所有条目收进 $Files / $Dirs（不含 $Path 自身）。 #>
    param([string]$Path, $Files, $Dirs)

    foreach ($child in @(Get-ChildItem -LiteralPath $Path -Force -ErrorAction SilentlyContinue)) {
        if ($child.PSIsContainer) {
            [void]$Dirs.Add($child.FullName)
            Add-TreeItems -Path $child.FullName -Files $Files -Dirs $Dirs
        }
        else {
            [void]$Files.Add($child.FullName)
        }
    }
}

function Get-PurgePlan {
    <#
      卸载时的删除清单。dry-run 与实际删除共用本函数，保证预览与实删一致。
      -PurgeData 保留 config\（用户偏好），-PurgeAll 全部删除。
    #>
    param([string]$Root, [string]$Mode)

    $plan = @{ mode = $Mode; root = $Root; files = @(); dirs = @(); kept = @(); rootRemovable = $false }

    if ($Mode -eq 'none' -or [string]::IsNullOrWhiteSpace($Root) -or -not (Test-Path -LiteralPath $Root)) {
        return $plan
    }

    $files = New-Object System.Collections.ArrayList
    $dirs = New-Object System.Collections.ArrayList
    $kept = New-Object System.Collections.ArrayList

    # 保留判定按"目录层级"做，绝不按路径前缀比较。
    # 传进来的 data 目录可能是 8.3 短名（例如 ...\ADMINI~1\...），而 Get-ChildItem 枚举出来的
    # FullName 是全名，两边前缀一比就全不匹配 —— config 会被当成可删项，用户偏好就没了。
    # 只认"根目录下第一层里名字叫 config 的那个目录"，与路径怎么写的无关。
    $keepConfig = ($Mode -eq 'data')
    $configDir = Join-Path $Root 'config'

    foreach ($child in @(Get-ChildItem -LiteralPath $Root -Force -ErrorAction SilentlyContinue)) {
        if ($keepConfig -and $child.PSIsContainer -and $child.Name -eq 'config') {
            foreach ($f in @(Get-ChildItem -LiteralPath $child.FullName -Recurse -Force -ErrorAction SilentlyContinue)) {
                if (-not $f.PSIsContainer) { [void]$kept.Add($f.FullName) }
            }
            continue
        }

        if ($child.PSIsContainer) {
            [void]$dirs.Add($child.FullName)
            Add-TreeItems -Path $child.FullName -Files $files -Dirs $dirs
        }
        else {
            [void]$files.Add($child.FullName)
        }
    }

    $plan.files = @($files)
    # 目录必须自底向上删：父目录非空时删除会失败
    $plan.dirs = @($dirs | Sort-Object -Property @{ Expression = { $_.Length } } -Descending)
    $plan.kept = @($kept)
    $plan.rootRemovable = ($plan.kept.Count -eq 0) -and (-not (Test-Path -LiteralPath $configDir))

    return $plan
}

function Invoke-Purge {
    param([hashtable]$Plan)

    $deletedFiles = 0
    $deletedDirs = 0
    $failed = New-Object System.Collections.ArrayList

    foreach ($file in $Plan.files) {
        try { [IO.File]::Delete($file); $deletedFiles++ }
        catch { [void]$failed.Add("$file : $($_.Exception.Message)") }
    }

    foreach ($dir in $Plan.dirs) {
        try { [IO.Directory]::Delete($dir, $false); $deletedDirs++ }
        catch { [void]$failed.Add("$dir : $($_.Exception.Message)") }
    }

    $rootRemoved = $false
    if ($Plan.rootRemovable) {
        try { [IO.Directory]::Delete($Plan.root, $false); $rootRemoved = $true }
        catch { [void]$failed.Add("$($Plan.root) : $($_.Exception.Message)") }
    }

    return @{ deletedFiles = $deletedFiles; deletedDirs = $deletedDirs; rootRemoved = $rootRemoved; failed = @($failed) }
}

if ($DryRun -and $Action -eq 'install') {
    # 只构造任务对象并回显配置，不写入任务计划程序。用于在无管理员权限时验证参数是否可用。
    $task = Build-Task -RunLevel 'Highest' -LogonType 'S4U'
    Write-Result @{
        ok          = $true
        action      = 'dryrun'
        taskName    = $TaskName
        taskPath    = $TaskPath
        execute     = $task.Actions[0].Execute
        arguments   = $task.Actions[0].Arguments
        workingDir  = $task.Actions[0].WorkingDirectory
        triggerTime = $Time
        runLevel    = 'Highest'
        logonType   = 'S4U'
        startWhenAvailable = $true
        restartCount = 2
    }
    exit 0
}

switch ($Action) {

    'install' {
        $existing = Get-ExistingTask
        $warning = $null
        $lastError = $null
        $installed = $false
        $runLevelUsed = $null
        $created = $false

        # 从高到低尝试：最高权限+S4U（需管理员）→ 普通权限+S4U → 普通权限+交互登录（免管理员）
        $attempts = @(
            @{ RunLevel = 'Highest'; LogonType = 'S4U';         Warn = $null },
            @{ RunLevel = 'Limited'; LogonType = 'S4U';         Warn = '当前环境不允许创建最高权限任务，已降级为普通权限；SATA 扩展统计与错误日志不可用，累计写入量仍可读取。' },
            @{ RunLevel = 'Limited'; LogonType = 'Interactive'; Warn = '任务已降级为普通权限且依赖用户登录：未登录时不会执行。如需免登录运行，请以管理员身份重新安装。' }
        )

        foreach ($attempt in $attempts) {
            try {
                $task = Build-Task -RunLevel $attempt.RunLevel -LogonType $attempt.LogonType

                if ($existing) {
                    Set-ScheduledTask -TaskPath $TaskPath -TaskName $TaskName -Action $task.Actions `
                        -Trigger $task.Triggers -Settings $task.Settings -Principal $task.Principal | Out-Null
                    $created = $false
                }
                else {
                    Register-ScheduledTask -TaskPath $TaskPath -TaskName $TaskName -Action $task.Actions `
                        -Trigger $task.Triggers -Settings $task.Settings -Principal $task.Principal | Out-Null
                    $created = $true
                }

                $runLevelUsed = $attempt.RunLevel
                $warning = $attempt.Warn
                $installed = $true
                break
            }
            catch {
                $lastError = $_.Exception.Message
            }
        }

        if (-not $installed) {
            Write-Result @{ ok = $false; action = 'install'; error = "注册计划任务失败：$lastError"; hint = '请以管理员身份运行后重试' }
            exit 1
        }

        $info = Get-ScheduledTaskInfo -TaskPath $TaskPath -TaskName $TaskName
        $current = Get-ExistingTask

        Write-Result @{
            ok          = $true
            action      = 'install'
            taskName    = $TaskName
            taskPath    = $TaskPath
            created     = $created
            updated     = (-not $created)
            runLevel    = $runLevelUsed
            time        = $Time
            state       = [string]$current.State
            nextRunTime = if ($info.NextRunTime) { $info.NextRunTime.ToString('o') } else { $null }
            warning     = $warning
        }
        exit 0
    }

    'status' {
        $task = Get-ExistingTask
        if (-not $task) {
            Write-Result @{
                ok        = $true
                action    = 'status'
                installed = $false
                taskName  = $TaskName
                taskPath  = $TaskPath
            }
            exit 0
        }

        $info = Get-ScheduledTaskInfo -TaskPath $TaskPath -TaskName $TaskName
        $runLevel = 'Limited'
        if ($task.Principal -and $task.Principal.RunLevel) { $runLevel = [string]$task.Principal.RunLevel }

        Write-Result @{
            ok            = $true
            action        = 'status'
            installed     = $true
            taskName      = $TaskName
            taskPath      = $TaskPath
            state         = [string]$task.State
            runLevel      = $runLevel
            lastRunTime   = if ($info.LastRunTime) { $info.LastRunTime.ToString('o') } else { $null }
            lastResult    = $info.LastTaskResult
            lastResultText= Convert-ResultCode -Code $info.LastTaskResult
            nextRunTime   = if ($info.NextRunTime) { $info.NextRunTime.ToString('o') } else { $null }
            missedRuns    = $info.NumberOfMissedRuns
        }
        exit 0
    }

    'run' {
        if (-not (Get-ExistingTask)) {
            Write-Result @{ ok = $false; action = 'run'; error = '任务未安装' }
            exit 1
        }
        Start-ScheduledTask -TaskPath $TaskPath -TaskName $TaskName
        Write-Result @{ ok = $true; action = 'run'; taskName = $TaskName }
        exit 0
    }

    'uninstall' {
        # 注意：这里只查询，不注销。注销动作必须放到 dry-run 分支之后 ——
        # 否则 uninstall --dry-run 会真的把任务删掉，而用户以为只是预览。
        $task = Get-ExistingTask

        $mode = 'none'
        if ($PurgeAll) { $mode = 'all' } elseif ($PurgeData) { $mode = 'data' }

        $plan = Get-PurgePlan -Root $DataDirectory -Mode $mode

        if ($DryRun) {
            # 只输出计划；卸载不可逆，预览先列出待删与待保留文件。
            Write-Result @{
                ok              = $true
                action          = 'uninstall'
                dryRun          = $true
                taskName        = $TaskName
                taskPath        = $TaskPath
                taskInstalled   = [bool]$task
                purgeMode       = $mode
                dataDirectory   = $DataDirectory
                willDeleteFiles = @($plan.files)
                willDeleteDirs  = @($plan.dirs)
                willKeep        = @($plan.kept)
                rootRemovable   = $plan.rootRemovable
            }
            exit 0
        }

        $removed = $false
        if ($task) {
            Unregister-ScheduledTask -TaskPath $TaskPath -TaskName $TaskName -Confirm:$false
            $removed = $true
        }

        $result = Invoke-Purge -Plan $plan

        $note = switch ($mode) {
            'all' {
                if ($result.rootRemoved) { '任务与全部数据已删除（含偏好设置）' }
                else { "已删除 $($result.deletedFiles) 个文件，目录未能整体移除" }
            }
            'data' {
                if ($result.failed.Count -eq 0) { '任务已移除，样本数据已删除，偏好设置保留' }
                else { "样本已删除，$($result.failed.Count) 项未能删除（可能被占用）" }
            }
            default { '任务已移除，数据已保留' }
        }

        Write-Result @{
            ok            = $true
            action        = 'uninstall'
            taskName      = $TaskName
            taskPath      = $TaskPath
            taskRemoved   = $removed
            purgeMode     = $mode
            dataRemoved   = ($mode -ne 'none' -and $result.failed.Count -eq 0 -and ($result.deletedFiles -gt 0 -or $result.rootRemoved))
            deletedFiles  = $result.deletedFiles
            deletedDirs   = $result.deletedDirs
            rootRemoved   = $result.rootRemoved
            kept          = @($plan.kept)
            dataNote      = $note
            errors        = @($result.failed)
        }
        exit 0
    }
}
