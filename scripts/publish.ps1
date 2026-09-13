param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',
    [switch]$SingleFile
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$publishName = if ($SingleFile) { "VivoPodsManager-$Runtime-single-file" } else { "VivoPodsManager-$Runtime" }
$publishPath = Join-Path $projectRoot "artifacts/$publishName"

$publishArguments = @(
    (Join-Path $projectRoot 'src/VivoPods.App/VivoPods.App.csproj')
    '-c', 'Release'
    '-r', $Runtime
    '--self-contained', 'true'
    '-p:DebugType=None'
)
if ($SingleFile) {
    $publishArguments += @(
        '-p:PublishSingleFile=true'
        '-p:IncludeNativeLibrariesForSelfExtract=true'
        '-p:EnableCompressionInSingleFile=true'
    )
} else {
    $publishArguments += '-p:PublishSingleFile=false'
}

dotnet publish @publishArguments -o $publishPath
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

if (-not $SingleFile) {
    Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md'), (Join-Path $projectRoot 'LICENSE'), (Join-Path $projectRoot 'THIRD_PARTY_NOTICES.md') -Destination $publishPath
    Copy-Item -LiteralPath (Join-Path $projectRoot 'docs') -Destination $publishPath -Recurse -Force
}

$archivePath = Join-Path $projectRoot "artifacts/$publishName.zip"
$archiveSource = if ($SingleFile) { Join-Path $publishPath 'VivoPodsManager.exe' } else { Join-Path $publishPath '*' }
Compress-Archive -Path $archiveSource -DestinationPath $archivePath -Force
Get-FileHash -LiteralPath $archivePath -Algorithm SHA256
$appKind = if ($SingleFile) { 'Single-file app' } else { 'Portable app' }
Write-Output "$appKind`: $publishPath/VivoPodsManager.exe"
