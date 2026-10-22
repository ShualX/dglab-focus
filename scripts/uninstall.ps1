param([string]$TargetPath = (Join-Path $env:LOCALAPPDATA 'Programs\DglabFocus'), [switch]$NoShortcuts)
$ErrorActionPreference = 'Stop'
$resolvedTarget = [IO.Path]::GetFullPath($TargetPath).TrimEnd('\')
$marker = Join-Path $resolvedTarget '.dglab-focus-install'
if (!(Test-Path -LiteralPath $marker)) { throw 'This directory was not installed by DglabFocus.' }
if ((Get-Item -LiteralPath $resolvedTarget -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Refusing to uninstall through a directory junction or symbolic link.' }
$recordedTarget = (Get-Content -LiteralPath $marker -Raw).Trim()
if ($recordedTarget -ne $resolvedTarget -or $resolvedTarget -eq [IO.Path]::GetPathRoot($resolvedTarget).TrimEnd('\')) { throw 'Installation path verification failed.' }
$targetExe = Join-Path $resolvedTarget 'DglabFocus.exe'
if (Get-Process -Name DglabFocus -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $targetExe }) { throw 'Close DglabFocus before uninstalling.' }
if (!$NoShortcuts) {
    $menuFolder = Join-Path ([Environment]::GetFolderPath('Programs')) 'DglabFocus'
    foreach ($shortcut in @((Join-Path $menuFolder 'DglabFocus.lnk'), (Join-Path ([Environment]::GetFolderPath('Desktop')) 'DglabFocus.lnk'))) {
        if (Test-Path -LiteralPath $shortcut) { Remove-Item -LiteralPath $shortcut -Force }
    }
    if ((Test-Path -LiteralPath $menuFolder) -and !(Get-ChildItem -LiteralPath $menuFolder -Force | Select-Object -First 1)) { Remove-Item -LiteralPath $menuFolder }
}
# Both the absolute target and its installation marker have been checked above.
# Study data lives in a separate LocalAppData directory and is preserved.
Remove-Item -LiteralPath $resolvedTarget -Recurse -Force
Write-Output 'Uninstalled. Your DglabFocus settings and study history have been preserved.'
