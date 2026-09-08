Add-Type -AssemblyName System.Drawing
$S = 512
$bmp = New-Object System.Drawing.Bitmap $S, $S
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'

function Rgb($r,$gg,$b) { [System.Drawing.Color]::FromArgb(255,$r,$gg,$b) }
function Argb($a,$r,$gg,$b) { [System.Drawing.Color]::FromArgb($a,$r,$gg,$b) }
function RoundRect($x,$y,$w,$h,$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddArc($x, $y, 2*$r, 2*$r, 180, 90)
    $p.AddArc($x+$w-2*$r, $y, 2*$r, 2*$r, 270, 90)
    $p.AddArc($x+$w-2*$r, $y+$h-2*$r, 2*$r, 2*$r, 0, 90)
    $p.AddArc($x, $y+$h-2*$r, 2*$r, 2*$r, 90, 90)
    $p.CloseFigure()
    return $p
}

# background -----------------------------------------------------------------
$g.FillRectangle((New-Object System.Drawing.SolidBrush (Rgb 24 23 22)), 0, 0, $S, $S)
$glowPath = New-Object System.Drawing.Drawing2D.GraphicsPath
$glowPath.AddEllipse(-60, -60, $S+120, $S+120)
$glow = New-Object System.Drawing.Drawing2D.PathGradientBrush $glowPath
$glow.CenterPoint = New-Object System.Drawing.PointF (($S/2), ($S/2 - 30))
$glow.CenterColor = (Argb 150 126 68 38)
$glow.SurroundColors = @((Argb 0 24 23 22))
$g.FillRectangle($glow, 0, 0, $S, $S)

$g.TranslateTransform(256, 292); $g.ScaleTransform(1.07, 1.07); $g.TranslateTransform(-256, -292)

# sound waves behind the unit --------------------------------------------------
foreach ($side in @(-1, 1)) {
    $cx = if ($side -lt 0) { 168 } else { 344 }
    $i = 0
    foreach ($r in @(90, 116, 143)) {
        $i++
        $a = 235 - ($i * 40)
        $pen = New-Object System.Drawing.Pen (Argb $a 214 74 46), (12 - 2*$i)
        $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
        $start = if ($side -lt 0) { 132 } else { -48 }
        $g.DrawArc($pen, ($cx - $r), (296 - $r), (2*$r), (2*$r), $start, 96)
        $pen.Dispose()
    }
}

# carry handle -----------------------------------------------------------------
$hp = New-Object System.Drawing.Pen (Rgb 72 66 60), 20
$hp.StartCap = 'Round'; $hp.EndCap = 'Round'
$g.DrawArc($hp, 176, 118, 160, 150, 190, 160)
$hp2 = New-Object System.Drawing.Pen (Argb 90 176 122 84), 6
$g.DrawArc($hp2, 176, 122, 160, 150, 195, 150)

# body -------------------------------------------------------------------------
$body = RoundRect 76 196 360 190 22
$bodyBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush `
    (New-Object System.Drawing.Point 0,196), (New-Object System.Drawing.Point 0,386), (Rgb 86 78 70), (Rgb 44 39 35)
$g.FillPath($bodyBrush, $body)
$edge = New-Object System.Drawing.Pen (Rgb 214 74 46), 8
$g.DrawPath($edge, $body)
$inner = RoundRect 88 208 336 166 16
$g.DrawPath((New-Object System.Drawing.Pen (Argb 60 232 190 150), 2), $inner)

# corrosion on the body -------------------------------------------------------
$g.SetClip($body)
$r2 = New-Object System.Random 77
for ($i = 0; $i -lt 220; $i++) {
    $x = $r2.Next(76, 436); $y = $r2.Next(196, 386)
    $d = $r2.Next(4, 26); $a = $r2.Next(6, 30)
    $b = New-Object System.Drawing.SolidBrush (Argb $a 186 96 52)
    $g.FillEllipse($b, $x, $y, $d, $d); $b.Dispose()
}
$hl = New-Object System.Drawing.Drawing2D.LinearGradientBrush `
    (New-Object System.Drawing.Point 0,196), (New-Object System.Drawing.Point 0,250), (Argb 70 255 226 198), (Argb 0 255 226 198)
$g.FillRectangle($hl, 76, 196, 360, 54)
$g.ResetClip()

# speakers ---------------------------------------------------------------------
foreach ($cx in @(168, 344)) {
    $g.FillEllipse((New-Object System.Drawing.SolidBrush (Rgb 26 24 22)), ($cx-62), 234, 124, 124)
    $g.DrawEllipse((New-Object System.Drawing.Pen (Rgb 206 66 43), 6), ($cx-62), 234, 124, 124)
    foreach ($rr in @(46, 34, 22)) {
        $g.DrawEllipse((New-Object System.Drawing.Pen (Argb 70 214 176 140), 2), ($cx-$rr), (296-$rr), (2*$rr), (2*$rr))
    }
    $cone = New-Object System.Drawing.Drawing2D.GraphicsPath
    $cone.AddEllipse(($cx-16), 280, 32, 32)
    $cb = New-Object System.Drawing.Drawing2D.PathGradientBrush $cone
    $cb.CenterColor = (Rgb 206 66 43)
    $cb.SurroundColors = @((Rgb 92 40 28))
    $g.FillEllipse($cb, ($cx-16), 280, 32, 32)
}

# centre panel: music note ------------------------------------------------------
$panel = RoundRect 226 244 60 104 10
$g.FillPath((New-Object System.Drawing.SolidBrush (Rgb 22 20 19)), $panel)
$g.DrawPath((New-Object System.Drawing.Pen (Argb 120 206 66 43), 3), $panel)
$noteBrush = New-Object System.Drawing.SolidBrush (Rgb 226 206 186)
$g.FillEllipse($noteBrush, 236, 300, 26, 20)
$stem = New-Object System.Drawing.Pen (Rgb 226 206 186), 6
$g.DrawLine($stem, 260, 310, 260, 262)
$flag = New-Object System.Drawing.Pen (Rgb 226 206 186), 6
$flag.EndCap = 'Round'
$g.DrawBezier($flag, (New-Object System.Drawing.Point 260,262), (New-Object System.Drawing.Point 274,266),
    (New-Object System.Drawing.Point 278,278), (New-Object System.Drawing.Point 272,290))

# feet + vignette ---------------------------------------------------------------
$foot = New-Object System.Drawing.SolidBrush (Rgb 42 38 34)
$g.FillRectangle($foot, 118, 386, 46, 14)
$g.FillRectangle($foot, 348, 386, 46, 14)

$g.ResetTransform()

$vig = New-Object System.Drawing.Drawing2D.GraphicsPath
$vig.AddEllipse(-110, -110, $S+220, $S+220)
$vb = New-Object System.Drawing.Drawing2D.PathGradientBrush $vig
$vb.CenterColor = (Argb 0 0 0 0)
$vb.SurroundColors = @((Argb 135 0 0 0))
$g.FillRectangle($vb, 0, 0, $S, $S)

# grain -------------------------------------------------------------------------
$rand = New-Object System.Random 20260908
for ($i = 0; $i -lt 70; $i++) {
    $x = $rand.Next(0,$S); $y = $rand.Next(0,$S); $len = $rand.Next(18,120); $a = $rand.Next(5,16)
    $p = New-Object System.Drawing.Pen (Argb $a 220 200 180), 1
    $g.DrawLine($p, $x, $y, ($x+$len), ($y+$rand.Next(-10,10))); $p.Dispose()
}
for ($i = 0; $i -lt 1100; $i++) {
    $x = $rand.Next(0,$S); $y = $rand.Next(0,$S); $a = $rand.Next(4,22)
    $b = New-Object System.Drawing.SolidBrush (Argb $a 198 128 76)
    $g.FillRectangle($b, $x, $y, 2, 2); $b.Dispose()
}

$bmp.Save("W:\SafeZoneMusic\icon.png", [System.Drawing.Imaging.ImageFormat]::Png)
foreach ($size in @(256, 128)) {
    $small = New-Object System.Drawing.Bitmap $size, $size
    $sg = [System.Drawing.Graphics]::FromImage($small)
    $sg.InterpolationMode = 'HighQualityBicubic'
    $sg.DrawImage($bmp, 0, 0, $size, $size)
    $small.Save("W:\SafeZoneMusic\icon-$size.png", [System.Drawing.Imaging.ImageFormat]::Png)
    $sg.Dispose(); $small.Dispose()
}
$g.Dispose(); $bmp.Dispose()
Write-Output "icon rendered at 512, 256 and 128"
