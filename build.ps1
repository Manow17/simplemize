# Builds Simplemize.exe with the Roslyn C# compiler that ships with Visual Studio / Build Tools.
# Output: .\dist\Simplemize.exe   (targets .NET Framework 4.8, which every Windows 10/11 PC already has)
#
#   .\build.ps1             release build
#   .\build.ps1 -Library    compile the core only (quick error check)

param([switch]$Library)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

function Find-Csc {
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        $found = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Roslyn\csc.exe' | Select-Object -First 1
        if ($found) { return $found }
    }
    $guess = Get-ChildItem "${env:ProgramFiles(x86)}\Microsoft Visual Studio", "$env:ProgramFiles\Microsoft Visual Studio" -Recurse -Filter csc.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\Roslyn\\' } | Select-Object -First 1
    if ($guess) { return $guess.FullName }
    throw "Roslyn csc.exe not found. Install 'Visual Studio Build Tools' (C# compiler)."
}

$csc = Find-Csc
$fx = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$wpf = "$fx\WPF"
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null

$refs = @(
    "$fx\System.dll", "$fx\System.Core.dll", "$fx\System.Xml.dll", "$fx\System.Xaml.dll",
    "$fx\System.Management.dll", "$fx\System.ServiceProcess.dll", "$fx\System.Web.Extensions.dll",
    "$fx\Microsoft.CSharp.dll", "$wpf\WindowsBase.dll", "$wpf\PresentationCore.dll", "$wpf\PresentationFramework.dll"
) | ForEach-Object { "/reference:`"$_`"" }

$sources = Get-ChildItem (Join-Path $root 'src') -Recurse -Filter *.cs | ForEach-Object { "`"$($_.FullName)`"" }
if ($Library) {
    $sources = Get-ChildItem (Join-Path $root 'src\Core') -Recurse -Filter *.cs | ForEach-Object { "`"$($_.FullName)`"" }
}

$common = @('/nologo', '/nostdlib-', '/langversion:latest', '/optimize+', '/deterministic', '/platform:anycpu',
            '/warn:4', '/nowarn:1701,1702,8019', '/utf8output', '/codepage:65001')

if ($Library) {
    $cscArgs = $common + @('/target:library', "/out:`"$dist\Simplemize.Core.dll`"") + $refs + $sources
} else {
    $resources = Get-ChildItem (Join-Path $root 'src\Ui') -Filter *.xaml | ForEach-Object { "/resource:`"$($_.FullName)`",Simplemize.$($_.Name)" }
    $cscArgs = $common + @('/target:winexe', "/out:`"$dist\Simplemize.exe`"",
        "/win32icon:`"$root\assets\simplemize.ico`"", "/win32manifest:`"$root\src\app.manifest`"") + $refs + $resources + $sources
}

$rsp = Join-Path $env:TEMP 'simplemize-build.rsp'
$cscArgs | Set-Content -Encoding UTF8 $rsp
& $csc /noconfig "@$rsp"
if ($LASTEXITCODE -ne 0) { throw "Build failed." }
if (-not $Library) {
    $exe = Get-Item "$dist\Simplemize.exe"
    "Built $($exe.FullName)  ($([math]::Round($exe.Length / 1KB)) KB)"
}
