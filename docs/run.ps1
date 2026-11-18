# Simplemize launcher
#
#   irm https://manow17.github.io/simplemize/run.ps1 | iex
#
# Downloads the latest Simplemize.exe from GitHub Releases into %LOCALAPPDATA%\Simplemize,
# checks it against the SHA-256 checksum published with the release, and starts it.
# Nothing else is downloaded or changed. Read it before you run it: it's short.

& {
    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue'   # the progress bar makes downloads very slow in Windows PowerShell
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

    $base = 'https://github.com/Manow17/simplemize/releases/latest/download'
    $dir  = Join-Path $env:LOCALAPPDATA 'Simplemize'
    $exe  = Join-Path $dir 'Simplemize.exe'
    $new  = "$exe.download"
    $sum  = "$exe.sha256"
    New-Item -ItemType Directory -Force -Path $dir | Out-Null

    Write-Host ''
    Write-Host '  simplemize' -ForegroundColor White
    Write-Host '  Downloading the latest version from GitHub...' -ForegroundColor DarkGray
    try {
        Invoke-WebRequest -Uri "$base/Simplemize.exe" -OutFile $new -UseBasicParsing
        Invoke-WebRequest -Uri "$base/Simplemize.exe.sha256" -OutFile $sum -UseBasicParsing
    } catch {
        Write-Host "  Download failed: $($_.Exception.Message)" -ForegroundColor Red
        return
    }

    $expected = ((Get-Content -Path $sum -Raw) -split '\s+')[0].Trim().ToLowerInvariant()
    $actual = (Get-FileHash -Path $new -Algorithm SHA256).Hash.ToLowerInvariant()
    Remove-Item -Path $sum -Force -ErrorAction SilentlyContinue
    if ($expected.Length -ne 64 -or $actual -ne $expected) {
        Remove-Item -Path $new -Force -ErrorAction SilentlyContinue
        Write-Host '  The download does not match the published checksum, so it was deleted and not started.' -ForegroundColor Red
        return
    }
    Write-Host "  Checksum OK  $actual" -ForegroundColor DarkGray

    try { Move-Item -Path $new -Destination $exe -Force }
    catch {
        Write-Host '  Simplemize is already open. Close it and run this again to update.' -ForegroundColor Yellow
        Remove-Item -Path $new -Force -ErrorAction SilentlyContinue
        return
    }

    Write-Host '  Starting Simplemize. Windows will ask for administrator rights.' -ForegroundColor DarkGray
    Write-Host ''
    Start-Process -FilePath $exe
}
