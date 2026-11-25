param(
    [string]$DotnetPath,
    [string]$MediaToolsDirectory,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $DotnetPath) {
    $localSdk = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $localSdk) { $DotnetPath = $localSdk } else { $DotnetPath = (Get-Command dotnet -ErrorAction Stop).Source }
}
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot 'artifacts\MediaTaxi-0.2.0-win-x64' }
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $outputPath) { throw 'Choose a new output directory; publication does not overwrite an existing build.' }
& $DotnetPath publish (Join-Path $projectRoot 'src\MediaTaxi\MediaTaxi.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $outputPath
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md'),(Join-Path $projectRoot 'VERSION') -Destination $outputPath
Copy-Item -LiteralPath (Join-Path $outputPath 'media-taxi.example.ini') -Destination (Join-Path $outputPath 'media-taxi.ini')
if (-not $MediaToolsDirectory) {
    $candidate = Join-Path $projectRoot '.tools\media'
    if (Test-Path -LiteralPath (Join-Path $candidate 'ffmpeg.exe')) { $MediaToolsDirectory = $candidate }
}
if ($MediaToolsDirectory) {
    $toolsPath = Join-Path $outputPath 'tools'
    New-Item -ItemType Directory -Path $toolsPath | Out-Null
    foreach ($name in @('ffmpeg.exe','ffprobe.exe')) {
        $source = Join-Path $MediaToolsDirectory $name
        if (-not (Test-Path -LiteralPath $source)) { throw "Missing media tool: $source" }
        Copy-Item -LiteralPath $source -Destination (Join-Path $toolsPath $name)
    }
    foreach ($notice in Get-ChildItem -LiteralPath $MediaToolsDirectory -File | Where-Object { $_.Name -match '^(LICENSE|COPYING|NOTICE|README|SOURCE)' }) {
        Copy-Item -LiteralPath $notice.FullName -Destination (Join-Path $toolsPath $notice.Name)
    }
}
$zipPath = $outputPath + '.zip'
if (Test-Path -LiteralPath $zipPath) { throw 'ZIP already exists; refusing to overwrite.' }
Compress-Archive -LiteralPath $outputPath -DestinationPath $zipPath -CompressionLevel Optimal
Write-Output "Published: $outputPath"
Write-Output "ZIP: $zipPath"
Write-Output 'Create/configure source/output folders and network destinations before running MediaTaxi.exe.'
