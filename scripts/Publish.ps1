param(
    [string]$DotnetPath,
    [string]$MediaToolsDirectory
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactRoot = Join-Path $projectRoot 'artifacts'
$current = Join-Path $artifactRoot 'media-taxi-current'
$zipPath = Join-Path $artifactRoot 'media-taxi-current.zip'
$stageRoot = Join-Path $projectRoot ('.tools\publish-stage\' + [Guid]::NewGuid().ToString('N'))
$stage = Join-Path $stageRoot 'media-taxi-current'

function Assert-ChildPath([string]$Candidate, [string]$Parent) {
    $resolved = [IO.Path]::GetFullPath($Candidate)
    $prefix = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Parent)) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Path escaped intended directory: $resolved" }
    return $resolved
}
function Assert-NoLinks([string]$Target) {
    if ((Get-Item -LiteralPath $Target -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Refusing a linked artifact path: $Target" }
    if (Get-ChildItem -LiteralPath $Target -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint } | Select-Object -First 1) { throw "Refusing artifact tree containing a link: $Target" }
}
function Resolve-State([string]$Ini) {
    $section = ''; $value = ''
    foreach ($line in Get-Content -LiteralPath $Ini) {
        if ($line -match '^\s*\[([^\]]+)\]') { $section = $Matches[1] }
        elseif ($section -ieq 'folders' -and $line -match '^\s*state\s*=\s*(.*?)\s*$') { $value = $Matches[1] }
    }
    if (-not $value) { throw 'Current INI requires folders.state.' }
    return [IO.Path]::GetFullPath($value, [IO.Path]::GetDirectoryName($Ini))
}
function Assert-ActivePaths([string]$Ini) {
    $section = ''
    foreach ($line in Get-Content -LiteralPath $Ini) {
        if ($line -match '^\s*\[([^\]]+)\]') { $section = $Matches[1]; continue }
        if ($line -notmatch '^\s*([^;#=]+?)\s*=\s*(.*?)\s*$') { continue }
        $key = $Matches[1]; $value = $Matches[2]
        if ($section -ine 'folders' -and -not ($section -ieq 'media' -and $key -in @('ffmpeg','ffprobe')) -and -not ($section -like 'destinations:*' -and $key -ieq 'path')) { continue }
        $resolved = [IO.Path]::GetFullPath($value, [IO.Path]::GetDirectoryName($Ini))
        if ($resolved.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { [void](Assert-ChildPath $resolved $current) }
    }
}
New-Item -ItemType Directory -Path (Join-Path $projectRoot '.tools'),$artifactRoot -Force | Out-Null
$publishLock = [IO.FileStream]::new((Join-Path $projectRoot '.tools\publish.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
$stateLock = $null
try {
if (Get-Process MediaTaxi -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) }) { throw 'Stop this Media Taxi console before publishing.' }
if (Test-Path -LiteralPath $current) { Assert-NoLinks $current }
$privateIni = Join-Path $current 'media-taxi.ini'
if (Test-Path -LiteralPath $privateIni) {
    Assert-ActivePaths $privateIni
    $state = Resolve-State $privateIni
    if ($state.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { [void](Assert-ChildPath $state $current) }
    New-Item -ItemType Directory -Path $state -Force | Out-Null
    $stateLock = [IO.FileStream]::new((Join-Path $state 'instance.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
}
if (-not $DotnetPath) {
    $localSdk = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $localSdk) { $DotnetPath = $localSdk } else { $DotnetPath = (Get-Command dotnet -ErrorAction Stop).Source }
}
$version = (Get-Content -LiteralPath (Join-Path $projectRoot 'VERSION') -Raw).Trim()
& $DotnetPath publish (Join-Path $projectRoot 'src\MediaTaxi\MediaTaxi.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $stage
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md'),(Join-Path $projectRoot 'VERSION') -Destination $stage
Copy-Item -LiteralPath (Join-Path $stage 'media-taxi.example.ini') -Destination (Join-Path $stage 'media-taxi.ini')
if (-not $MediaToolsDirectory) {
    $candidate = Join-Path $projectRoot '.tools\media'
    if (Test-Path -LiteralPath (Join-Path $candidate 'ffmpeg.exe')) { $MediaToolsDirectory = $candidate }
}
if ($MediaToolsDirectory) {
    $toolsPath = Join-Path $stage 'tools'
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
# Package a clean staging tree before touching the private INI or runtime state.
$programFiles = Get-ChildItem -LiteralPath $stage -File -Recurse | ForEach-Object { [IO.Path]::GetRelativePath($stage, $_.FullName) } | Where-Object { $_ -ne 'media-taxi.ini' }
$programFiles | Set-Content -LiteralPath (Join-Path $stage '.build-files.txt') -Encoding utf8
$stagedZip = Join-Path $stageRoot 'media-taxi-current.zip'
Compress-Archive -LiteralPath $stage -DestinationPath $stagedZip -CompressionLevel Optimal
New-Item -ItemType Directory -Path $current -Force | Out-Null
$manifest = Join-Path $current '.build-files.txt'
if (Test-Path -LiteralPath $manifest) {
    foreach ($oldFile in Get-Content -LiteralPath $manifest) {
        if (-not $oldFile -or $oldFile -eq 'media-taxi.ini' -or $oldFile -match '^runtime([/\\]|$)') { continue }
        if ($programFiles -contains $oldFile) { continue }
        $obsolete = Assert-ChildPath (Join-Path $current $oldFile) $current
        $protectedRuntime = Join-Path $current 'runtime'
        if ($obsolete -eq $privateIni -or $obsolete -eq $protectedRuntime -or $obsolete.StartsWith($protectedRuntime + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Program manifest refers to private INI/runtime data.' }
        if (Test-Path -LiteralPath $obsolete -PathType Leaf) { Remove-Item -LiteralPath $obsolete -Force }
    }
}
foreach ($file in Get-ChildItem -LiteralPath $stage -File -Recurse -Force) {
    $relative = [IO.Path]::GetRelativePath($stage, $file.FullName)
    if ($relative -eq 'media-taxi.ini' -and (Test-Path -LiteralPath $privateIni)) { continue }
    $target = Assert-ChildPath (Join-Path $current $relative) $current
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $target -Force
}
& (Join-Path $current 'MediaTaxi.exe') --validate-config
if ($LASTEXITCODE -ne 0) { throw 'Current private INI failed validation; old artifacts were retained.' }
Copy-Item -LiteralPath $stagedZip -Destination $zipPath -Force
# Current data/settings are now independent of every superseded generated output.
foreach ($old in Get-ChildItem -LiteralPath $artifactRoot -Force) {
    if ($old.Name -in @('media-taxi-current','media-taxi-current.zip')) { continue }
    $obsolete = Assert-ChildPath $old.FullName $artifactRoot
    Assert-NoLinks $obsolete
    if ($old.PSIsContainer) { Remove-Item -LiteralPath $obsolete -Recurse -Force }
    else { Remove-Item -LiteralPath $obsolete -Force }
}
Write-Output "Published $version`: $current"
Write-Output "ZIP: $zipPath"
Write-Output 'Preserved private media-taxi.ini/runtime state. ZIP contains generic settings only.'
}
finally {
    if ($stateLock) { $stateLock.Dispose() }
    $publishLock.Dispose()
    if (Test-Path -LiteralPath $stageRoot) { $verifiedStage = Assert-ChildPath $stageRoot (Join-Path $projectRoot '.tools\publish-stage'); Assert-NoLinks $verifiedStage; Remove-Item -LiteralPath $verifiedStage -Recurse -Force }
}
