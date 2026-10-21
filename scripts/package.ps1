param([string]$RuntimeVersion = '8.0.32', [switch]$SkipPublish)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$publishRoot = Join-Path $projectRoot 'dist\win-x64'
$bundleRoot = Join-Path $projectRoot 'dist\dglab-focus-windows-x64'
if (!$SkipPublish) {
  dotnet publish (Join-Path $projectRoot 'src\Focus.Windows\Focus.Windows.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:RuntimeFrameworkVersion=$RuntimeVersion -p:DebugType=None -o $publishRoot --nologo
  if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
}
New-Item -ItemType Directory -Force -Path $bundleRoot | Out-Null
Copy-Item -Path (Join-Path $publishRoot '*') -Destination $bundleRoot -Force
foreach ($name in @('README.md','LICENSE','THIRD_PARTY_NOTICES.md')) { Copy-Item -LiteralPath (Join-Path $projectRoot $name) -Destination $bundleRoot -Force }
Copy-Item -LiteralPath (Join-Path $projectRoot 'extension') -Destination $bundleRoot -Recurse -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'licenses') -Destination $bundleRoot -Recurse -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs') -Destination $bundleRoot -Recurse -Force
$archive = Join-Path $projectRoot 'dist\dglab-focus-windows-x64.zip'
Compress-Archive -Path (Join-Path $bundleRoot '*') -DestinationPath $archive -Force
Get-FileHash -LiteralPath $archive -Algorithm SHA256 | ForEach-Object { ($_.Hash.ToLowerInvariant() + '  dglab-focus-windows-x64.zip') | Set-Content -LiteralPath (Join-Path $projectRoot 'dist\SHA256SUMS.txt') -Encoding ascii }
Write-Output $archive
