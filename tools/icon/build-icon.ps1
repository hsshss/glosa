# Builds src/Glosa.App/Assets/glosa.ico from the two SVGs beside this script, each size
# rendered from its own: glosa-small.svg (redrawn so the meter's segments and the keys do
# not run together into grey) for 32 px and below, glosa.svg above.
#
# The renderer is headless Edge, which every Windows 11 machine has. Each size is stored
# as PNG, which Windows reads inside .ico.

$ErrorActionPreference = 'Stop'

$edge = "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe"
if (-not (Test-Path $edge)) { throw "Edge not found at $edge" }

$here = $PSScriptRoot
$out = Join-Path $here '..\..\src\Glosa.App\Assets\glosa.ico'
$work = Join-Path ([System.IO.Path]::GetTempPath()) "glosa-icon-$PID"
New-Item -ItemType Directory -Force $work | Out-Null

$sizes = @(
    @{ Size = 16;  Svg = 'glosa-small.svg' },
    @{ Size = 24;  Svg = 'glosa-small.svg' },
    @{ Size = 32;  Svg = 'glosa-small.svg' },
    @{ Size = 48;  Svg = 'glosa.svg' },
    @{ Size = 64;  Svg = 'glosa.svg' },
    @{ Size = 128; Svg = 'glosa.svg' },
    @{ Size = 256; Svg = 'glosa.svg' }
)

try {
    $images = foreach ($s in $sizes) {
        $n = $s.Size
        $svg = (Get-Content (Join-Path $here $s.Svg) -Raw) -replace '<svg ', "<svg width=`"$n`" height=`"$n`" "
        $html = Join-Path $work "$n.html"
        $png = Join-Path $work "$n.png"
        Set-Content $html -Encoding utf8 -Value "<!doctype html><html><body style=`"margin:0;overflow:hidden`">$svg</body></html>"

        # The screenshot is on disk only once Edge has exited, hence -Wait.
        $edgeArgs = @('--headless', '--disable-gpu', '--hide-scrollbars',
                  '--force-device-scale-factor=1', '--default-background-color=00000000',
                  "--user-data-dir=$work\profile", "--window-size=$n,$n",
                  "--screenshot=$png", ([System.Uri]$html).AbsoluteUri)
        Start-Process $edge -ArgumentList $edgeArgs -Wait -WindowStyle Hidden

        if (-not (Test-Path $png)) { throw "Edge did not render $n px" }
        $bytes = [System.IO.File]::ReadAllBytes($png)
        # The PNG header's IHDR holds the size; check Edge drew what was asked.
        $w = [System.Net.IPAddress]::NetworkToHostOrder([BitConverter]::ToInt32($bytes, 16))
        $h = [System.Net.IPAddress]::NetworkToHostOrder([BitConverter]::ToInt32($bytes, 20))
        if ($w -ne $n -or $h -ne $n) { throw "Edge rendered $n px as ${w}x$h" }
        @{ Size = $n; Bytes = $bytes }
    }

    # ICONDIR, then one ICONDIRENTRY per image, then the images. A size of 256 is written as 0.
    $stream = New-Object System.IO.MemoryStream
    $wr = New-Object System.IO.BinaryWriter $stream
    $wr.Write([uint16]0); $wr.Write([uint16]1); $wr.Write([uint16]$images.Count)
    $offset = 6 + 16 * $images.Count
    foreach ($i in $images) {
        $b = [byte]($i.Size % 256)
        $wr.Write($b); $wr.Write($b); $wr.Write([byte]0); $wr.Write([byte]0)
        $wr.Write([uint16]1); $wr.Write([uint16]32)
        $wr.Write([uint32]$i.Bytes.Length); $wr.Write([uint32]$offset)
        $offset += $i.Bytes.Length
    }
    foreach ($i in $images) { $wr.Write($i.Bytes) }
    $wr.Flush()
    [System.IO.File]::WriteAllBytes($out, $stream.ToArray())
    Write-Host "Wrote $((Resolve-Path $out).Path) ($($images.Count) sizes)"
}
finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
