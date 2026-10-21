param([ValidateSet('Install','HostNew','HostSave','Guest','Logs','Export')][string]$Action = 'Install', [string]$GameRoot, [string]$SaveName)
$ErrorActionPreference = 'Stop'
try {
    . (Join-Path $PSScriptRoot 'Demo-Common.ps1')
    $manifest = Get-CoopManifest $PSScriptRoot
    $GameRoot = Find-CoopGameRoot $GameRoot $PSScriptRoot
    $bin = Join-Path $GameRoot 'bin/Win64_Shipping_Client'
    if ($Action -eq 'Export') { & (Join-Path $PSScriptRoot 'Export-Diagnostics.ps1') -GameRoot $GameRoot; exit $LASTEXITCODE }
    if ($Action -eq 'Logs') { Start-Process explorer.exe -ArgumentList ('"'+$bin+'"') -WindowStyle Normal; exit 0 }
    if (Get-Process -Name Bannerlord -ErrorAction SilentlyContinue) { throw '骑砍 2 正在运行。请先退出游戏，再安装或启动；不会替换运行中的文件。' }
    if ((Get-CoopSha256 (Join-Path $bin 'TaleWorlds.CampaignSystem.dll')) -ne $manifest.requiredCampaignDllSha256) { throw "游戏版本不匹配，需要 $($manifest.gameVersion)。尚未安装或启动。" }
    $module = Get-CoopPackagePath $GameRoot 'Modules/Coop'
    if ($Action -eq 'Install') {
        Assert-CoopPhysicalPath $module
        if (Test-Path -LiteralPath $module) {
            $backup = Get-CoopPackagePath $GameRoot ('CoopDemoBackups/'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8)+'/Coop')
            Assert-CoopPhysicalPath $backup
            New-Item -ItemType Directory -Path (Split-Path -Parent $backup) | Out-Null
            Move-Item -LiteralPath $module -Destination $backup
            Write-Host "旧 Mod 已完整备份（不在 Modules 内）：$backup"
        }
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Modules/Coop') -Destination $module -Recurse
        Get-ChildItem -LiteralPath $module -Recurse -File | Unblock-File
    }
    foreach ($entry in $manifest.files | Where-Object { $_.path.StartsWith('Modules/Coop/') }) {
        $path = Get-CoopPackagePath $GameRoot $entry.path
        if (!(Test-Path -LiteralPath $path) -or (Get-CoopSha256 $path) -ne $entry.sha256) { throw "已安装文件不匹配：$($entry.path)，请先运行 0-Install.cmd。" }
    }
    @{gameRoot=$GameRoot} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'demo-local.json') -Encoding UTF8
    if ($Action -eq 'Install') { Write-Host "安装校验完成，版本 $($manifest.codeCommit)。请启动 Steam，双方使用同一测试包。"; exit 0 }
    $tokens = @('/singleplayer','_MODULES_*Native*SandBoxCore*SandBox*StoryMode*Coop*_MODULES_')
    if ($Action -eq 'Guest') {
        $tokens += '/client'
        Write-Host '加入方：在游戏主菜单等待房主的 Steam 邀请，先不要创建或加入其他 Coop 会话。'
    } else {
        $tokens += '/listenhost'
        if ($Action -eq 'HostSave') {
            if (!$SaveName) { $SaveName = Read-Host '输入使用同一 Steam 房主身份创建的联机存档名（不含 .sav）' }
            if (!$SaveName -or $SaveName -notmatch '^[\p{L}\p{N}_. -]+$' -or $SaveName.EndsWith('.sav')) { throw '存档名无效；只输入名称，不要输入路径或 .sav 扩展名。' }
            $saveRoot = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Mount and Blade II Bannerlord/Game Saves'
            foreach ($extension in @('.sav','.json')) {
                if (!(Test-Path -LiteralPath (Join-Path $saveRoot ($SaveName+$extension)))) { throw "缺少存档组件 $extension，不修改或替换原存档。" }
            }
            $tokens += @('/coopsave',('"'+$SaveName+'"'))
            Write-Host '房主：加载指定存档；请确认它属于当前 Steam 身份。测试包不会改写旧存档身份。'
        } else { Write-Host '房主：主菜单点击 Host Co-op Sandbox 创建新战役。进入大地图后按 Esc → 邀请 Steam 好友。' }
    }
    $recordDir = Join-Path $PSScriptRoot ('UserLogs/'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8))
    New-Item -ItemType Directory -Path (Join-Path $recordDir 'before-launch') | Out-Null
    $beforeLogs = @(foreach ($log in Get-CoopRawLogs $bin) {
        Copy-CoopLogSnapshot $log.FullName (Join-Path $recordDir ('before-launch/'+$log.Name)) $false
    })
    @{capturedUtc=[DateTime]::UtcNow.ToString('O');logs=$beforeLogs} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $recordDir 'before-launch.json') -Encoding UTF8
    $process = Start-Process -FilePath (Join-Path $bin 'Bannerlord.exe') -WorkingDirectory $bin -ArgumentList ($tokens -join ' ') -WindowStyle Normal -PassThru
    @{role=$Action;pid=$process.Id;commit=$manifest.codeCommit;startedUtc=[DateTime]::UtcNow.ToString('O')} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $recordDir 'launch.json') -Encoding UTF8
    Write-Host "游戏已启动，PID $($process.Id)。出现问题时运行 5-Export.cmd，将诊断 ZIP 发给开发者。"
} catch { Write-Host ("操作失败："+$_.Exception.Message) -ForegroundColor Red; exit 1 }
