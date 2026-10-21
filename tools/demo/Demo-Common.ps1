$ErrorActionPreference = 'Stop'
function Get-CoopSha256([string]$Path) {
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    $hash = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($hash.ComputeHash($stream))).Replace('-','').ToLowerInvariant() }
    finally { $hash.Dispose(); $stream.Dispose() }
}
function Get-CoopPackagePath([string]$Root, [string]$Relative) {
    $prefix = [IO.Path]::GetFullPath($Root).TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
    $path = [IO.Path]::GetFullPath((Join-Path $Root $Relative))
    if ([IO.Path]::IsPathRooted($Relative) -or !$path.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)) { throw "包内文件路径不合法：$Relative" }
    return $path
}
function Assert-CoopPhysicalPath([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "拒绝替换链接目录：$current。请使用实际游戏安装路径。" }
        $parent = [IO.Path]::GetDirectoryName($current)
        if ($parent -eq $current) { break }
        $current = $parent
    }
}
function Get-CoopManifest([string]$Root) {
    $manifest = Get-Content -LiteralPath (Join-Path $Root 'manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if (!$manifest.codeCommit -or !$manifest.requiredCampaignDllSha256 -or !$manifest.files -or !($manifest.files | Where-Object { $_.path -eq 'Modules/Coop/SubModule.xml' })) { throw '测试包清单缺少版本或 Mod 文件，请重新解压完整测试包。' }
    foreach ($entry in $manifest.files) {
        $path = Get-CoopPackagePath $Root $entry.path
        if (!(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-CoopSha256 $path) -ne $entry.sha256) { throw "文件校验失败：$($entry.path)。请重新解压同一版本完整测试包。" }
    }
    return $manifest
}
function Find-CoopGameRoot([string]$Root, [string]$PackageRoot) {
    $config = Join-Path $PackageRoot 'demo-local.json'
    if (!$Root -and (Test-Path -LiteralPath $config)) { $Root = (Get-Content -LiteralPath $config -Raw -Encoding UTF8 | ConvertFrom-Json).gameRoot }
    if (!$Root) {
        $steam = Get-ItemProperty -LiteralPath 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue
        $libraries = @()
        if ($steam.SteamPath) { $libraries += $steam.SteamPath }
        if (${env:ProgramFiles(x86)}) { $libraries += (Join-Path ${env:ProgramFiles(x86)} 'Steam') }
        foreach ($library in @($libraries)) {
            $vdf = Join-Path $library 'steamapps/libraryfolders.vdf'
            if (Test-Path -LiteralPath $vdf) {
                foreach ($match in [regex]::Matches([IO.File]::ReadAllText($vdf), '"path"\s+"([^"]+)"')) { $libraries += $match.Groups[1].Value.Replace('\\','\') }
            }
        }
        foreach ($library in $libraries) {
            $candidate = Join-Path $library 'steamapps/common/Mount & Blade II Bannerlord'
            if (Test-Path -LiteralPath (Join-Path $candidate 'bin/Win64_Shipping_Client/Bannerlord.exe')) { $Root = $candidate; break }
        }
    }
    if (!$Root) { $Root = (Read-Host '输入骑砍 2 游戏根目录（包含 bin 和 Modules）').Trim().Trim('"') }
    $Root = [IO.Path]::GetFullPath($Root).TrimEnd('\','/')
    if (!(Test-Path -LiteralPath (Join-Path $Root 'bin/Win64_Shipping_Client/Bannerlord.exe'))) { throw '没有找到 Bannerlord.exe，请选择游戏根目录。' }
    return $Root
}

function Get-CoopRawLogs([string]$Bin) {
    return @(Get-ChildItem -LiteralPath $Bin -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'Coop*.log*' -or $_.Name -eq 'BootPatches.log' })
}
function Copy-CoopLogSnapshot([string]$Source, [string]$Destination, [bool]$IsRunning) {
    $before = Get-Item -LiteralPath $Source
    $inputStream = [IO.File]::Open($Source,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite)
    $outputStream = [IO.File]::Open($Destination,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try {
        $remaining = $inputStream.Length
        $buffer = New-Object byte[] 65536
        while ($remaining -gt 0) {
            $read = $inputStream.Read($buffer,0,[int][Math]::Min($remaining,$buffer.Length))
            if ($read -eq 0) { break }
            $outputStream.Write($buffer,0,$read); $remaining -= $read
        }
    } finally { $outputStream.Dispose(); $inputStream.Dispose() }
    $after = Get-Item -LiteralPath $Source
    return @{source=$Source;sourceLengthBefore=$before.Length;sourceLastWriteUtcBefore=$before.LastWriteTimeUtc.ToString('O');sourceLengthAfter=$after.Length;sourceLastWriteUtcAfter=$after.LastWriteTimeUtc.ToString('O');copiedBytes=(Get-Item -LiteralPath $Destination).Length;sha256=(Get-CoopSha256 $Destination);liveSnapshot=($IsRunning -or $before.Length -ne $after.Length -or $before.LastWriteTimeUtc -ne $after.LastWriteTimeUtc)}
}
