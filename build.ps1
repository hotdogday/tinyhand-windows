# Builds dist\TinyHand.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
# No SDK or Visual Studio needed. Usage:  powershell -ExecutionPolicy Bypass -File build.ps1
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw 'csc.exe from .NET Framework 4 was not found.' }

# App icon: a multi-size .ico (PNG entries) made from the logo.
$ico = Join-Path $root 'assets\icon\app.ico'
if (-not (Test-Path $ico)) {
    Add-Type -AssemblyName System.Drawing
    $src = [System.Drawing.Image]::FromFile((Join-Path $root 'assets\icon\mark-512.png'))
    $sizes = 256, 64, 48, 32, 24, 16
    $pngs = foreach ($s in $sizes) {
        $bmp = New-Object System.Drawing.Bitmap $s, $s
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.InterpolationMode = 'HighQualityBicubic'
        $g.PixelOffsetMode = 'HighQuality'
        $g.DrawImage($src, 0, 0, $s, $s)
        $g.Dispose()
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        , $ms.ToArray()
    }
    $src.Dispose()
    $out = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $out
    $w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $s = $sizes[$i]; $dim = if ($s -ge 256) { 0 } else { $s }
        $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([uint16]1); $w.Write([uint16]32)
        $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset)
        $offset += $pngs[$i].Length
    }
    foreach ($p in $pngs) { $w.Write([byte[]]$p) }
    $w.Flush()
    [System.IO.File]::WriteAllBytes($ico, $out.ToArray())
    Write-Host "made $ico"
}

$resources = @()
$resources += Get-ChildItem (Join-Path $root 'assets\hands\*.png')
$resources += Get-ChildItem (Join-Path $root 'assets\sounds\*.wav')
$resources += Get-ChildItem (Join-Path $root 'assets\icon\menubar_*@3x.png')
$resources += Get-Item (Join-Path $root 'assets\icon\mark-512.png')
$resources += Get-Item $ico
$resArgs = $resources | ForEach-Object { "/resource:$($_.FullName),TinyHand.$($_.Name)" }

$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null
$sources = Get-ChildItem (Join-Path $root 'src\*.cs') | ForEach-Object { $_.FullName }

& $csc /nologo /target:winexe /codepage:65001 /optimize+ /platform:anycpu /warn:4 `
    "/out:$(Join-Path $dist 'TinyHand.exe')" `
    "/win32icon:$ico" "/win32manifest:$(Join-Path $root 'src\app.manifest')" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
    @resArgs @sources
if ($LASTEXITCODE -ne 0) { throw "build failed ($LASTEXITCODE)" }
$size = [math]::Round((Get-Item (Join-Path $dist 'TinyHand.exe')).Length / 1MB, 1)
Write-Host "built dist\TinyHand.exe ($size MB)"
