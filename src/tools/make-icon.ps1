# Icon generator.
#   make-icon.ps1 -Sheet <png>     comparison sheet of all variants
#   make-icon.ps1 -Variant <n>     writes src\GameDub\gamedub.ico from variant n
param([string]$Sheet, [int]$Variant = 0)
Add-Type -AssemblyName System.Drawing

$Dark  = [System.Drawing.Color]::FromArgb(255, 20, 23, 26)
$Green = [System.Drawing.Color]::FromArgb(255, 16, 124, 16)
$Lime  = [System.Drawing.Color]::FromArgb(255, 61, 220, 74)
$White = [System.Drawing.Color]::White

function RoundRect([double]$x, [double]$y, [double]$w, [double]$h, [double]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = [Math]::Max(0.5, $r * 2)
    $p.AddArc([float]$x, [float]$y, [float]$d, [float]$d, 180, 90)
    $p.AddArc([float]($x + $w - $d), [float]$y, [float]$d, [float]$d, 270, 90)
    $p.AddArc([float]($x + $w - $d), [float]($y + $h - $d), [float]$d, [float]$d, 0, 90)
    $p.AddArc([float]$x, [float]($y + $h - $d), [float]$d, [float]$d, 90, 90)
    $p.CloseFigure(); return $p
}

function Brush($c) { New-Object System.Drawing.SolidBrush $c }

# Centered voice waveform (5 rounded bars)
function Wave($g, [double]$s, [double]$cx, [double]$cy, [double]$scale, $color) {
    $bw = $s * 0.085 * $scale; $gap = $s * 0.05 * $scale
    $hs = 0.22, 0.44, 0.62, 0.44, 0.22
    $total = 5 * $bw + 4 * $gap
    for ($i = 0; $i -lt 5; $i++) {
        $h = $s * $hs[$i] * $scale
        $x = $cx - $total / 2 + $i * ($bw + $gap)
        $g.FillPath((Brush $color), (RoundRect $x ($cy - $h / 2) $bw $h ($bw / 2)))
    }
}

function Draw([int]$v, [int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.TextRenderingHint = 'AntiAliasGridFit'
    $bg = RoundRect 0 0 ($s - 1) ($s - 1) ($s * 0.22)
    switch ($v) {
        1 { # voice waveform, lime on dark
            $g.FillPath((Brush $Dark), $bg); Wave $g $s ($s / 2) ($s / 2) 1 $Lime }
        2 { # Latin A -> Cyrillic Ya: English to Ukrainian/Russian
            $g.FillPath((Brush $Dark), $bg)
            $f = New-Object System.Drawing.Font 'Segoe UI Semibold', ([float]($s * 0.46)), ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
            $sf = New-Object System.Drawing.StringFormat; $sf.Alignment = 'Center'; $sf.LineAlignment = 'Center'
            $g.DrawString('A', $f, (Brush $White), [System.Drawing.RectangleF]::new([float]($s * 0.05), [float]($s * 0.02), [float]($s * 0.48), [float]$s), $sf)
            $g.DrawString([string][char]0x042F, $f, (Brush $Lime), [System.Drawing.RectangleF]::new([float]($s * 0.47), [float]($s * 0.02), [float]($s * 0.48), [float]$s), $sf) }
        3 { # speaker with sound arcs
            $g.FillPath((Brush $Dark), $bg)
            $w = Brush $White
            $g.FillPath($w, (RoundRect ($s * 0.18) ($s * 0.40) ($s * 0.13) ($s * 0.20) ($s * 0.025)))
            $g.FillPolygon($w, [System.Drawing.PointF[]]@(
                [System.Drawing.PointF]::new([float]($s * 0.29), [float]($s * 0.40)),
                [System.Drawing.PointF]::new([float]($s * 0.47), [float]($s * 0.25)),
                [System.Drawing.PointF]::new([float]($s * 0.47), [float]($s * 0.75)),
                [System.Drawing.PointF]::new([float]($s * 0.29), [float]($s * 0.60))))
            $pen = New-Object System.Drawing.Pen $Lime, ([float]($s * 0.065)); $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
            foreach ($r in 0.14, 0.27) {
                $g.DrawArc($pen, [float]($s * (0.50 - $r)), [float]($s * (0.5 - $r)), [float]($s * 2 * $r), [float]($s * 2 * $r), -42, 84) } }
        4 { # white waveform on Xbox-green circle
            $g.FillEllipse((Brush $Green), 0, 0, $s - 1, $s - 1); Wave $g $s ($s / 2) ($s / 2) 0.95 $White }
        5 { # white waveform inside a lime ring on dark
            $g.FillPath((Brush $Dark), $bg)
            $pw = [Math]::Max(1.2, $s * 0.07)
            $pen = New-Object System.Drawing.Pen $Lime, ([float]$pw)
            $m = $s * 0.14
            $g.DrawEllipse($pen, [float]$m, [float]$m, [float]($s - 2 * $m), [float]($s - 2 * $m))
            Wave $g $s ($s / 2) ($s / 2) 0.62 $White }
    }
    $g.Dispose(); return $bmp
}

$count = 5
if ($Sheet) {
    $tile = 230
    $sheetBmp = New-Object System.Drawing.Bitmap ($tile * $count), 300
    $g = [System.Drawing.Graphics]::FromImage($sheetBmp)
    $g.Clear([System.Drawing.Color]::FromArgb(255, 14, 16, 18)); $g.TextRenderingHint = 'AntiAliasGridFit'
    $font = New-Object System.Drawing.Font 'Segoe UI Semibold', 20, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    for ($v = 1; $v -le $count; $v++) {
        $x = ($v - 1) * $tile + 25
        $big = Draw $v 180; $g.DrawImage($big, $x, 20); $big.Dispose()
        $m = Draw $v 48; $g.DrawImage($m, $x, 220); $m.Dispose()
        $sm = Draw $v 32; $g.DrawImage($sm, $x + 60, 228); $sm.Dispose()
        $t = Draw $v 16; $g.DrawImage($t, $x + 104, 236); $t.Dispose()
        $g.DrawString("$v", $font, [System.Drawing.Brushes]::White, $x + 150, 232)
    }
    $g.Dispose(); $sheetBmp.Save($Sheet); $sheetBmp.Dispose()
}

if ($Variant -gt 0) {
    $sizes = 16, 24, 32, 48, 64, 128, 256
    $pngs = foreach ($s in $sizes) {
        $b = Draw $Variant $s
        $ms = New-Object System.IO.MemoryStream
        $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $b.Dispose(); , $ms.ToArray()
    }
    $ms = New-Object System.IO.MemoryStream; $w = New-Object System.IO.BinaryWriter $ms
    $w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $s = $sizes[$i]
        $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256)); $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset)
        $offset += $pngs[$i].Length
    }
    foreach ($p in $pngs) { $w.Write($p) }
    [System.IO.File]::WriteAllBytes('C:\GameDub\src\GameDub\gamedub.ico', $ms.ToArray())
}
