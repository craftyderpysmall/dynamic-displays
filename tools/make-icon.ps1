# Generates src/SmartMonitor.App/Assets/app.ico (multi-size, PNG-compressed entries).
# Run from the repo root:  powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
Add-Type -AssemblyName System.Drawing

$out = Join-Path $PSScriptRoot "..\src\SmartMonitor.App\Assets\app.ico"
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null

function New-Frame([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $u = $s / 32.0

    function RoundRect($x, $y, $w, $h, $r) {
        $p = New-Object System.Drawing.Drawing2D.GraphicsPath
        $d = 2 * $r
        $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
        $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
        $p.CloseFigure(); return $p
    }

    $gray   = [System.Drawing.Color]::FromArgb(255, 120, 130, 145)
    $accent = [System.Drawing.Color]::FromArgb(255, 0, 120, 212)
    $light  = [System.Drawing.Color]::FromArgb(255, 96, 180, 255)

    # back monitor (left, gray outline)
    $pen = New-Object System.Drawing.Pen $gray, ([Math]::Max(1.0, 2 * $u))
    $g.DrawPath($pen, (RoundRect (1*$u) (5*$u) (16*$u) (12*$u) (2*$u)))
    # front monitor (right, filled accent)
    $g.FillPath((New-Object System.Drawing.SolidBrush $accent), (RoundRect (11*$u) (9*$u) (20*$u) (15*$u) (2.5*$u)))
    $g.FillPath((New-Object System.Drawing.SolidBrush $light), (RoundRect (13*$u) (11*$u) (16*$u) (11*$u) (1.5*$u)))
    # stand
    $g.FillRectangle((New-Object System.Drawing.SolidBrush $accent), 19*$u, 24*$u, 4*$u, 3*$u)
    $g.FillRectangle((New-Object System.Drawing.SolidBrush $accent), 16*$u, 27*$u, 10*$u, 2*$u)
    # cursor arrow on the front monitor
    $pts = @(
        (New-Object System.Drawing.PointF (19*$u), (12.5*$u)),
        (New-Object System.Drawing.PointF (19*$u), (21*$u)),
        (New-Object System.Drawing.PointF (21.2*$u), (18.8*$u)),
        (New-Object System.Drawing.PointF (23*$u), (22*$u)),
        (New-Object System.Drawing.PointF (24.4*$u), (21.2*$u)),
        (New-Object System.Drawing.PointF (22.6*$u), (18*$u)),
        (New-Object System.Drawing.PointF (25.5*$u), (18*$u))
    )
    $g.FillPolygon([System.Drawing.Brushes]::White, $pts)
    $g.DrawPolygon((New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 20, 40, 70)), ([Math]::Max(0.6, 0.8 * $u))), $pts)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    if ($s -ge 256) {
        # Large entry: PNG-compressed (supported by Windows Vista+).
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    } else {
        # Small entries: classic 32-bpp DIB (BITMAPINFOHEADER + bottom-up BGRA + AND mask), readable by everything.
        $w = New-Object System.IO.BinaryWriter $ms
        $w.Write([UInt32]40); $w.Write([Int32]$s); $w.Write([Int32]($s * 2))
        $w.Write([UInt16]1); $w.Write([UInt16]32); $w.Write([UInt32]0)
        $w.Write([UInt32]($s * $s * 4)); $w.Write([Int32]0); $w.Write([Int32]0); $w.Write([UInt32]0); $w.Write([UInt32]0)
        for ($y = $s - 1; $y -ge 0; $y--) {
            for ($x = 0; $x -lt $s; $x++) {
                $c = $bmp.GetPixel($x, $y)
                $w.Write([byte]$c.B); $w.Write([byte]$c.G); $w.Write([byte]$c.R); $w.Write([byte]$c.A)
            }
        }
        $maskRow = [int]([Math]::Ceiling($s / 32.0) * 4)
        $w.Write((New-Object byte[] ($maskRow * $s)))
        $w.Flush()
    }
    $bmp.Dispose()
    return ,$ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$frames = $sizes | ForEach-Object { ,(New-Frame $_) }

$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $data = $frames[$i]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$data.Length); $bw.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($d in $frames) { $bw.Write($d) }
$bw.Close()
Write-Host "Wrote $out"
