param([ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$publishPath = Join-Path $projectRoot "artifacts/VivoPodsManager-$Runtime"
dotnet publish (Join-Path $projectRoot 'src/VivoPods.App/VivoPods.App.csproj') -c Release -r $Runtime --self-contained true -p:PublishSingleFile=false -p:DebugType=None -o $publishPath
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md'), (Join-Path $projectRoot 'LICENSE'), (Join-Path $projectRoot 'THIRD_PARTY_NOTICES.md') -Destination $publishPath
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs') -Destination $publishPath -Recurse -Force
$archivePath = Join-Path $projectRoot "artifacts/VivoPodsManager-$Runtime.zip"
Compress-Archive -Path (Join-Path $publishPath '*') -DestinationPath $archivePath -Force
Get-FileHash -LiteralPath $archivePath -Algorithm SHA256
Write-Output "Portable app: $publishPath/VivoPodsManager.exe"
