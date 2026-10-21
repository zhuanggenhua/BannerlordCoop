param([Parameter(Mandatory=$true)][string]$GameRoot, [string]$OutputRoot)
$ErrorActionPreference = 'Stop'
try {
    . (Join-Path $PSScriptRoot 'Demo-Common.ps1')
    $manifest = Get-CoopManifest $PSScriptRoot
    if (!$OutputRoot) { $OutputRoot = Join-Path $PSScriptRoot 'Diagnostics' }
    $name = 'Coop-Diagnostics-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8)
    $destination = Get-CoopPackagePath $OutputRoot $name
    New-Item -ItemType Directory -Path (Join-Path $destination 'raw') | Out-Null
    $processes = @(Get-Process -Name Bannerlord -ErrorAction SilentlyContinue | ForEach-Object { @{pid=$_.Id;cpuSeconds=$_.CPU;workingSetBytes=$_.WorkingSet64} })
    $records = @()
    $bin = Join-Path $GameRoot 'bin/Win64_Shipping_Client'
    $sources = @(Get-CoopRawLogs $bin)
    foreach ($source in $sources) {
        $records += Copy-CoopLogSnapshot $source.FullName (Join-Path $destination ('raw/'+$source.Name)) ($processes.Count -gt 0)
    }
    $hints = @()
    $recent = @()
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $destination 'raw') -File) {
        $recent += @("$($file.Name) 最近连接记录（原文节选）：")
        $recent += @(Select-String -LiteralPath $file.FullName -Pattern 'Steam|联机|邀请|大厅|隧道|连接' | Select-Object -Last 5 | ForEach-Object { $_.Line })
        $recent += @("$($file.Name) 最近错误记录（原文节选）：")
        $recent += @(Select-String -LiteralPath $file.FullName -Pattern '\[ERR\]|Exception|失败|异常' | Select-Object -Last 3 | ForEach-Object { $_.Line })
        foreach ($pattern in @(@{regex='ERR|Exception|失败|异常';label='发现错误或异常线索，需结合完整调用栈确认'},@{regex='Steam|Lobby|大厅|邀请';label='发现 Steam 或大厅记录，不能单凭关键词判定连接失败'},@{regex='Epoch|Barrier|Freeze|冻结';label='发现任期或交接记录，需核对完整时间线'})) {
            if (Select-String -LiteralPath $file.FullName -Pattern $pattern.regex -Quiet) { $hints += "$($file.Name)：$($pattern.label)" }
        }
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'manifest.json') -Destination (Join-Path $destination 'package-manifest.json')
    if (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'UserLogs')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'UserLogs') -Destination (Join-Path $destination 'launch-records') -Recurse }
    @{capturedUtc=[DateTime]::UtcNow.ToString('O');codeCommit=$manifest.codeCommit;gameVersion=$manifest.gameVersion;moduleIdentities=$manifest.actualModuleIdentities;processes=$processes;logs=$records} | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $destination 'evidence.json') -Encoding UTF8
    $summary = @(
        '联机测试诊断摘要',
        "采集时间（UTC）：$([DateTime]::UtcNow.ToString('O'))",
        "测试包代码提交（非进程实测）：$($manifest.codeCommit)",
        "测试包要求的游戏版本：$($manifest.gameVersion)",
        "采集时游戏 PID：$(($processes | ForEach-Object {$_.pid}) -join ', ')",
        "原始日志数：$($records.Count)",
        '日志为采集时副本，游戏运行时不保证文件间原子一致；未翻译或删除原始异常。',
        'MVID 与提交版本来自包清单，不冒充进程实际加载版本；以下关键词仅为线索，不代表根因结论：'
    )
    $summary + $hints + $recent | Set-Content -LiteralPath (Join-Path $destination '中文摘要.txt') -Encoding UTF8
    $hashes = @(Get-ChildItem -LiteralPath $destination -File -Recurse | ForEach-Object { (Get-CoopSha256 $_.FullName)+'  '+$_.FullName.Substring($destination.Length+1) })
    $hashes | Set-Content -LiteralPath (Join-Path $destination 'SHA256.txt') -Encoding UTF8
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($destination,($destination+'.zip'))
    Write-Host "诊断已导出：$destination.zip"
    Write-Host '请先检查原始日志是否包含你的个人信息，再手动发给开发者；脚本不会上传。'
    exit 0
} catch { Write-Host ("诊断导出失败："+$_.Exception.Message) -ForegroundColor Red; exit 1 }
