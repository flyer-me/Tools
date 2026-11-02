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

if ($DryRun) {
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
        $task = Get-ExistingTask
        $removed = $false

        if ($task) {
            Unregister-ScheduledTask -TaskPath $TaskPath -TaskName $TaskName -Confirm:$false
            $removed = $true
        }

        $dataRemoved = $false
        $dataNote = '数据已保留'

        if ($PurgeData -and $DataDirectory -and (Test-Path $DataDirectory)) {
            try {
                Get-ChildItem -Path $DataDirectory -Recurse -File | Remove-Item -Force -Confirm:$false
                Get-ChildItem -Path $DataDirectory -Recurse -Directory | Sort-Object FullName -Descending |
                    Remove-Item -Force -Recurse -Confirm:$false
                Remove-Item -Path $DataDirectory -Force -Confirm:$false
                $dataRemoved = $true
                $dataNote = '数据已删除'
            }
            catch {
                $dataNote = "数据删除失败: $($_.Exception.Message)"
            }
        }

        Write-Result @{
            ok            = $true
            action        = 'uninstall'
            taskName      = $TaskName
            taskPath      = $TaskPath
            taskRemoved   = $removed
            dataRemoved   = $dataRemoved
            dataNote      = $dataNote
        }
        exit 0
    }
}
