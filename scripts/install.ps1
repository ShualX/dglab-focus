param(
    [string]$TargetPath = (Join-Path $env:LOCALAPPDATA 'Programs\DglabFocus'),
    [switch]$NoShortcuts,
    [switch]$NoLaunch
)
$ErrorActionPreference = 'Stop'
$bundlePath = Split-Path -Parent $PSScriptRoot
if (!(Test-Path -LiteralPath (Join-Path $bundlePath 'DglabFocus.exe'))) { throw 'Please extract the complete Windows release before installing.' }
$resolvedTarget = [IO.Path]::GetFullPath($TargetPath).TrimEnd('\')
if ($resolvedTarget -eq [IO.Path]::GetPathRoot($resolvedTarget).TrimEnd('\')) { throw 'The installation target cannot be a drive root.' }
$resolvedBundle = [IO.Path]::GetFullPath($bundlePath).TrimEnd('\')
if ($resolvedTarget -eq $resolvedBundle -or $resolvedTarget.StartsWith($resolvedBundle + '\', [StringComparison]::OrdinalIgnoreCase) -or $resolvedBundle.StartsWith($resolvedTarget + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Choose an installation directory outside the extracted release.'
}
$targetExe = Join-Path $resolvedTarget 'DglabFocus.exe'
if ((Test-Path -LiteralPath $resolvedTarget) -and ((Get-Item -LiteralPath $resolvedTarget -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Do not install into a directory junction or symbolic link.' }
$running = Get-Process -Name DglabFocus -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $targetExe }
if ($running) { throw 'Close the installed DglabFocus app before updating it.' }
if ((Test-Path -LiteralPath $resolvedTarget) -and !(Test-Path -LiteralPath (Join-Path $resolvedTarget '.dglab-focus-install')) -and (Get-ChildItem -LiteralPath $resolvedTarget -Force | Select-Object -First 1)) {
    throw 'The target contains other files. Choose an empty directory.'
}
New-Item -ItemType Directory -Path $resolvedTarget -Force | Out-Null
foreach ($entry in Get-ChildItem -LiteralPath $bundlePath -Force) { Copy-Item -LiteralPath $entry.FullName -Destination $resolvedTarget -Recurse -Force }
$resolvedTarget | Set-Content -LiteralPath (Join-Path $resolvedTarget '.dglab-focus-install') -Encoding UTF8
if (!$NoShortcuts) {
    $shortcutShell = New-Object -ComObject WScript.Shell
    $menuFolder = Join-Path ([Environment]::GetFolderPath('Programs')) 'DglabFocus'
    New-Item -ItemType Directory -Path $menuFolder -Force | Out-Null
    foreach ($shortcutPath in @((Join-Path $menuFolder 'DglabFocus.lnk'), (Join-Path ([Environment]::GetFolderPath('Desktop')) 'DglabFocus.lnk'))) {
        $shortcut = $shortcutShell.CreateShortcut($shortcutPath)
        $shortcut.TargetPath = $targetExe
        $shortcut.WorkingDirectory = $resolvedTarget
        $shortcut.IconLocation = "$targetExe,0"
        $shortcut.Save()
    }
}
Write-Output "Installed: $targetExe"
if (!$NoLaunch) { Start-Process -FilePath $targetExe -WindowStyle Normal }
