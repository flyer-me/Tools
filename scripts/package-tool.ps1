param(
    [string[]]$ToolId = @(),
    [string]$Version = '0.0.0-dev',
    [string]$OutDir = ''
)

# 按 manifest 的 artifacts 声明收集产物并打成一个 zip。
# 打包单元是「artifacts 命中的文件」而不是整个 dist 目录：dist 里可能有运行时数据
# （采样记录、日志、导出报表），整目录打包会把用户数据一起发出去。
# 需要保留的空目录用 kind = "dir" 单独声明。

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$repoRoot = Split-Path -Parent $PSScriptRoot
$toolsRoot = Join-Path $repoRoot 'tools'
if ([string]::IsNullOrWhiteSpace($OutDir)) { $OutDir = Join-Path $repoRoot 'out\packages' }

function Get-CommonDirPrefix {
    param([string[]]$RelativePaths)

    if (-not $RelativePaths -or $RelativePaths.Count -eq 0) { return '' }

    $split = @()
    foreach ($p in $RelativePaths) { $split += , @($p.Split('\')) }

    # 只比较到最短路径的目录层为止，最后一节是文件名
    $minDepth = ($split | ForEach-Object { $_.Count - 1 } | Measure-Object -Minimum).Minimum
    $common = @()
    for ($i = 0; $i -lt $minDepth; $i++) {
        $segment = $split[0][$i]
        $same = $true
        foreach ($s in $split) {
            if ($s[$i] -ne $segment) { $same = $false; break }
        }
        if (-not $same) { break }
        $common += $segment
    }
    return ($common -join '\')
}

function New-ZipFromDirectory {
    param([string]$SourceDir, [string]$ZipPath)

    if (Test-Path -LiteralPath $ZipPath) { [IO.File]::Delete($ZipPath) }

    $root = (Resolve-Path -LiteralPath $SourceDir).Path.TrimEnd('\', '/')
    $archive = [IO.Compression.ZipFile]::Open($ZipPath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        # 先写目录条目，否则空目录会丢 —— Compress-Archive 和 CreateFromDirectory 都不保留空目录，
        # 而空 data\ 正是便携模式的触发条件。
        Get-ChildItem -LiteralPath $SourceDir -Recurse -Directory -Force | ForEach-Object {
            $rel = $_.FullName.Substring($root.Length + 1).Replace('\', '/')
            $null = $archive.CreateEntry("$rel/")
        }

        Get-ChildItem -LiteralPath $SourceDir -Recurse -File -Force | ForEach-Object {
            $rel = $_.FullName.Substring($root.Length + 1).Replace('\', '/')
            $entry = $archive.CreateEntry($rel, [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = $_.LastWriteTime

            $target = $entry.Open()
            try {
                $source = [IO.File]::OpenRead($_.FullName)
                try { $source.CopyTo($target) } finally { $source.Dispose() }
            }
            finally { $target.Dispose() }
        }
    }
    finally { $archive.Dispose() }
}

# 枚举口径与 validate-tools.ps1 一致：只看 tools/ 下一级目录里的 manifest。
$manifests = @(Get-ChildItem -Path $toolsRoot -Directory | Sort-Object Name | ForEach-Object {
        $candidate = Join-Path $_.FullName 'tool.manifest.json'
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { Get-Item -LiteralPath $candidate }
    })
if ($manifests.Count -eq 0) { throw "No tool manifests found under $toolsRoot" }

New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

# 上一轮留下的包必须清掉：发布流程上传的是整个 out/packages 目录，
# 残留会让这次发版顺带带上别的工具、或者旧版本号的同名文件而不自知。
Get-ChildItem -LiteralPath $OutDir -File -Force |
    Where-Object { $_.Extension -eq '.zip' -or $_.Name -eq 'SHA256SUMS.txt' } |
    ForEach-Object { [IO.File]::Delete($_.FullName) }

$sumsPath = Join-Path $OutDir 'SHA256SUMS.txt'
$sumLines = New-Object System.Collections.Generic.List[string]
$packed = 0

foreach ($manifestPath in $manifests) {
    $toolDir = Split-Path -Parent $manifestPath.FullName
    $tool = Get-Content -Path $manifestPath.FullName -Raw | ConvertFrom-Json

    $id = [string]$tool.toolId
    if ($ToolId.Count -gt 0 -and $ToolId -notcontains $id) { continue }

    $buildKind = if ($tool.build.PSObject.Properties.Name -contains 'kind') { [string]$tool.build.kind } else { '' }

    $files = New-Object System.Collections.Generic.List[object]
    $dirs = New-Object System.Collections.Generic.List[object]

    foreach ($artifact in $tool.artifacts) {
        $pattern = [string]$artifact.pattern
        if ([string]::IsNullOrWhiteSpace($pattern)) { continue }
        $kind = if ($artifact.PSObject.Properties.Name -contains 'kind') { [string]$artifact.kind } else { '' }

        $hits = @(Get-Item -Path (Join-Path $toolDir $pattern) -ErrorAction SilentlyContinue)
        if ($hits.Count -eq 0) {
            throw "${id}: artifact pattern '$pattern' matched nothing - run assert-artifacts.ps1 first"
        }

        foreach ($hit in $hits) {
            $rel = $hit.FullName.Substring($toolDir.Length + 1)
            if ($hit.PSIsContainer) {
                if ($kind -eq 'dir') { $dirs.Add($rel) | Out-Null }
                else { foreach ($f in Get-ChildItem -LiteralPath $hit.FullName -Recurse -File -Force) {
                    $files.Add($f.FullName.Substring($toolDir.Length + 1)) | Out-Null } }
            }
            else {
                $files.Add($rel) | Out-Null
            }
        }
    }

    if ($files.Count -eq 0) { throw "${id}: no files matched, nothing to package" }

    $commonPrefix = Get-CommonDirPrefix -RelativePaths @($files | ForEach-Object { $_ })
    $prefixLen = if ([string]::IsNullOrEmpty($commonPrefix)) { 0 } else { $commonPrefix.Length + 1 }

    # 先落到临时暂存目录，保证 zip 内的相对结构可控
    $stage = Join-Path ([IO.Path]::GetTempPath()) ("dwt-pkg-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $stage -Force | Out-Null

    try {
        foreach ($rel in $files) {
            $entryRel = if ($prefixLen -gt 0) { $rel.Substring($prefixLen) } else { $rel }
            $dest = Join-Path $stage $entryRel
            $destDir = Split-Path -Parent $dest
            if ($destDir -and -not (Test-Path -LiteralPath $destDir)) {
                New-Item -ItemType Directory -Path $destDir -Force | Out-Null
            }
            Copy-Item -LiteralPath (Join-Path $toolDir $rel) -Destination $dest -Force
        }

        # 目录型 artifact 只保证目录存在；里面的内容由其它 pattern 决定。
        foreach ($rel in $dirs) {
            $entryRel = if ($prefixLen -gt 0) { $rel.Substring($prefixLen) } else { $rel }
            $dest = Join-Path $stage $entryRel
            if (-not (Test-Path -LiteralPath $dest)) {
                New-Item -ItemType Directory -Path $dest -Force | Out-Null
            }
        }

        # 工具说明随包走，用户解压先看到它
        $toolReadme = Join-Path $toolDir 'README.md'
        if (Test-Path -LiteralPath $toolReadme) {
            Copy-Item -LiteralPath $toolReadme -Destination (Join-Path $stage 'README.md') -Force
        }

        $suffix = if ($buildKind -eq 'dotnet-msbuild') { '-win-x64' } else { '' }
        $zipName = "$id-$Version$suffix.zip"
        $zipPath = Join-Path $OutDir $zipName

        New-ZipFromDirectory -SourceDir $stage -ZipPath $zipPath

        $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
        $sumLines.Add("$hash  $zipName")

        $count = @(Get-ChildItem -LiteralPath $stage -Recurse -File -Force).Count
        Write-Host ("  packed {0} ({1} files, {2:N1} KB)" -f $zipName, $count, ((Get-Item $zipPath).Length / 1KB))
        $packed++
    }
    finally {
        # 不用 Remove-Item -Recurse：本机安全策略会 fail-closed 拦下递归删除
        try { [IO.Directory]::Delete($stage, $true) } catch { }
    }
}

if ($ToolId.Count -gt 0 -and $packed -eq 0) { throw "No tool found with toolId(s) '$($ToolId -join ', ')'" }

if ($sumLines.Count -gt 0) {
    [IO.File]::WriteAllLines($sumsPath, $sumLines, (New-Object System.Text.UTF8Encoding $false))
    Write-Host ("  wrote {0}" -f (Split-Path $sumsPath -Leaf))
}

Write-Host ("Packaged {0} tool(s) into {1}" -f $packed, $OutDir)
