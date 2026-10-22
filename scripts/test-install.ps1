param([string]$BundlePath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'dist\dglab-focus-windows-x64'))
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\')
$testRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot ('artifacts\install-check-' + [Guid]::NewGuid().ToString('N'))))
if (!$testRoot.StartsWith($projectRoot + '\artifacts\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Test path verification failed.' }
$installTarget = Join-Path $testRoot 'application'
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
try {
    & (Join-Path $BundlePath 'setup\install.ps1') -TargetPath $installTarget -NoLaunch -NoShortcuts
    $installed = Join-Path $installTarget 'DglabFocus.exe'
    if (!(Test-Path -LiteralPath $installed)) { throw 'Installed executable missing' }
    if ((Get-FileHash -LiteralPath $installed).Hash -ne (Get-FileHash -LiteralPath (Join-Path $BundlePath 'DglabFocus.exe')).Hash) { throw 'Installed executable differs from package' }
    if (!(Test-Path -LiteralPath (Join-Path $installTarget 'extension\manifest.json'))) { throw 'Installed browser extension missing' }
    'keep' | Set-Content -LiteralPath (Join-Path $testRoot 'preserved.json')
    & (Join-Path $BundlePath 'setup\uninstall.ps1') -TargetPath $installTarget -NoShortcuts
    if ((Test-Path -LiteralPath $installTarget) -or !(Test-Path -LiteralPath (Join-Path $testRoot 'preserved.json'))) { throw 'Uninstall touched the wrong directory' }
    $foreign = Join-Path $testRoot 'foreign'
    New-Item -ItemType Directory -Path $foreign | Out-Null
    'keep' | Set-Content -LiteralPath (Join-Path $foreign 'unrelated.txt')
    $refused = $false
    try { & (Join-Path $BundlePath 'setup\install.ps1') -TargetPath $foreign -NoLaunch -NoShortcuts } catch { $refused = $true }
    if (!$refused -or !(Test-Path -LiteralPath (Join-Path $foreign 'unrelated.txt'))) { throw 'Installer overwrote an unrelated directory' }
    Write-Output 'PASS: installed complete executable and extension, verified hashes, uninstalled only its owned directory, refused foreign files.'
}
finally {
    # Resolved above and restricted to this workspace's unique artifacts/install-check directory.
    if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}
