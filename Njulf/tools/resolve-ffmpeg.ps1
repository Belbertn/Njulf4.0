# Build-machine dependency only; stdout is consumed as an executable path by MSBuild.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
# dotnet/MSBuild can inherit PowerShell 7's PSModulePath when launching Windows PowerShell.
Import-Module "$PSHOME/Modules/Microsoft.PowerShell.Utility/Microsoft.PowerShell.Utility.psd1"

$installed = Get-Command ffmpeg -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
if ($installed) { Write-Output $installed.Source; exit 0 }
if (-not [Environment]::Is64BitOperatingSystem) {
    throw 'Automatic FFmpeg download requires 64-bit Windows. Supply -p:NjulfFFmpeg=<executable>.'
}

$version = '9.0.1'
$archiveHash = 'fec81ae03971d9dd4be3ebe02e263bd2ec1d789483f931bdba5f5715e65da2e9'
$uri = "https://github.com/GyanD/codexffmpeg/releases/download/$version/ffmpeg-$version-essentials_build.zip"
$cache = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../artifacts/tools/ffmpeg-$version"))
$executable = Join-Path $cache "ffmpeg-$version-essentials_build/bin/ffmpeg.exe"
$marker = Join-Path $cache 'verified.sha256'
[IO.Directory]::CreateDirectory($cache) | Out-Null

# Serialize concurrent project builds; publish the marker only after extraction succeeds.
$lock = $null
$deadline = [DateTime]::UtcNow.AddMinutes(10)
while (-not $lock) {
    try { $lock = [IO.File]::Open((Join-Path $cache '.lock'), 'OpenOrCreate', 'ReadWrite', 'None') }
    catch [IO.IOException] {
        if ([DateTime]::UtcNow -ge $deadline) { throw 'Timed out waiting for FFmpeg tool acquisition.' }
        Start-Sleep -Milliseconds 250
    }
}
$archive = Join-Path $cache 'download.zip'
try {
    if (-not ((Test-Path -LiteralPath $executable) -and (Test-Path -LiteralPath $marker) -and
        ((Get-Content -LiteralPath $marker -Raw).Trim() -eq (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash))) {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -UseBasicParsing -Uri $uri -OutFile $archive
        if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $archiveHash) {
            throw 'FFmpeg archive SHA-256 mismatch. Download rejected.'
        }
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zip = [IO.Compression.ZipFile]::OpenRead($archive)
        try {
            foreach ($entry in $zip.Entries) {
                # Keep upstream licenses and documentation, but omit unused utilities.
                if (-not $entry.Name -or $entry.Name -in @('ffplay.exe', 'ffprobe.exe')) { continue }
                $target = [IO.Path]::GetFullPath((Join-Path $cache $entry.FullName))
                if (-not $target.StartsWith($cache + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
                    throw 'FFmpeg archive contains an invalid path.'
                }
                [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
            }
        } finally { $zip.Dispose() }
        (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash | Set-Content -LiteralPath $marker
    }
    Write-Output $executable
} finally {
    if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
    $lock.Dispose()
}
