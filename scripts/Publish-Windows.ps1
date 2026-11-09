param(
    [Parameter(Mandatory = $true)][string]$FfmpegArchive,
    [string]$DotnetPath = 'dotnet',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../publish/win-x64')
)

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$archivePath = (Resolve-Path -LiteralPath $FfmpegArchive).Path
$expectedHash = '60F467265B1E312373DBCD92200C2618A74850F98D3D078E94296BB3FA2047BA'
if ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash -ne $expectedHash) { throw 'FFmpeg package SHA-256 verification failed.' }
$destination = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $destination) { throw 'Output directory already exists. Choose a new output directory.' }

& $DotnetPath publish (Join-Path $repo 'BiliDesktop/BiliDesktop.csproj') -c Release -r win-x64 --self-contained true -o $destination
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
$toolDirectory = Join-Path $destination 'vendor/ffmpeg'
New-Item -ItemType Directory -Path $toolDirectory -Force | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
try {
    foreach ($spec in @(@('/bin/ffmpeg.exe', 'ffmpeg.exe'), @('/bin/ffprobe.exe', 'ffprobe.exe'), @('/LICENSE', 'LICENSE'))) {
        $matches = @($archive.Entries | Where-Object { $_.FullName.EndsWith($spec[0], [StringComparison]::Ordinal) -and ($_.FullName.ToCharArray() | Where-Object { $_ -eq '/' }).Count -eq $(if ($spec[1] -eq 'LICENSE') { 1 } else { 2 }) })
        if ($matches.Count -ne 1 -or $matches[0].Length -le 0 -or $matches[0].Length -gt 200MB) { throw "Invalid FFmpeg archive entry: $($spec[1])" }
        $inputStream = $matches[0].Open()
        $outputStream = [IO.File]::Open((Join-Path $toolDirectory $spec[1]), [IO.FileMode]::CreateNew)
        try { $inputStream.CopyTo($outputStream) } finally { $outputStream.Dispose(); $inputStream.Dispose() }
    }
}
finally { $archive.Dispose() }
Copy-Item -LiteralPath (Join-Path $repo 'README.md') -Destination (Join-Path $destination 'README.md')
Write-Output "Self-contained release with FFmpeg: $destination"
