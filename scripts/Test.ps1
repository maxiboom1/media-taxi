param([string]$DotnetPath, [string]$MediaToolsDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $DotnetPath) {
    $localSdk = Join-Path $projectRoot '.tools/dotnet/dotnet.exe'
    if (Test-Path -LiteralPath $localSdk) { $DotnetPath = $localSdk }
    else { $DotnetPath = (Get-Command dotnet -ErrorAction Stop).Source }
}
if (-not $MediaToolsDirectory) { $MediaToolsDirectory = Join-Path $projectRoot '.tools/media' }
$priorFfmpeg = $env:MEDIATAXI_FFMPEG
$priorFfprobe = $env:MEDIATAXI_FFPROBE
try {
    foreach ($name in @('ffmpeg','ffprobe')) {
        $file = Join-Path $MediaToolsDirectory "$name.exe"
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Required codec tests cannot be skipped: missing $file" }
        Set-Item -LiteralPath "Env:MEDIATAXI_$($name.ToUpperInvariant())" -Value ([IO.Path]::GetFullPath($file))
    }
    & $DotnetPath test (Join-Path $projectRoot 'MediaTaxi.sln') -c Release --logger 'console;verbosity=minimal'
    if ($LASTEXITCODE -ne 0) { throw 'Release tests failed.' }
    & node --experimental-vm-modules (Join-Path $PSScriptRoot 'TestDashboard.mjs')
    if ($LASTEXITCODE -ne 0) { throw 'Dashboard checks failed.' }
}
finally {
    $env:MEDIATAXI_FFMPEG = $priorFfmpeg
    $env:MEDIATAXI_FFPROBE = $priorFfprobe
}
