Add-Type -AssemblyName System.Drawing

function New-RoundedPath([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-IconBitmap([int]$S) {
    $bmp = New-Object System.Drawing.Bitmap($S, $S, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $f = [single]$S
    $inset = $f * 0.025
    $side = $f - 2 * $inset
    $radius = $f * 0.215

    # --- Vault body: rounded tile in the app's header navy, lit from the top-left.
    $body = New-RoundedPath $inset $inset $side $side $radius
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, 0)),
        (New-Object System.Drawing.PointF($f, $f)),
        [System.Drawing.Color]::FromArgb(255, 45, 58, 79),
        [System.Drawing.Color]::FromArgb(255, 20, 26, 36))
    $g.FillPath($grad, $body)

    # Hairline rim keeps the tile from dissolving into a dark taskbar.
    if ($S -ge 24) {
        $rimPen = New-Object System.Drawing.Pen(
            [System.Drawing.Color]::FromArgb(56, 255, 255, 255), [single]($f * 0.022))
        $g.DrawPath($rimPen, $body)
        $rimPen.Dispose()
    }

    $cx = $f / 2.0
    $cy = $f / 2.0
    $accent = [System.Drawing.Color]::FromArgb(255, 59, 130, 246)

    # --- Dial notches: only legible above 48px, so they are drawn there and nowhere else.
    if ($S -ge 48) {
        $nPen = New-Object System.Drawing.Pen(
            [System.Drawing.Color]::FromArgb(150, 124, 147, 184), [single]($f * 0.028))
        $nPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $nPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        for ($i = 0; $i -lt 8; $i++) {
            $a = [Math]::PI * 2 * $i / 8.0
            $r1 = $f * 0.365
            $r2 = $f * 0.415
            $g.DrawLine($nPen,
                [single]($cx + [Math]::Cos($a) * $r1), [single]($cy + [Math]::Sin($a) * $r1),
                [single]($cx + [Math]::Cos($a) * $r2), [single]($cy + [Math]::Sin($a) * $r2))
        }
        $nPen.Dispose()
    }

    # Below 24px the spokes and the ring merge into a blob, so the glyph drops to a bare
    # dial — one ring, one hub — rather than a smaller version of the full handwheel.
    $tiny = $S -lt 24

    # --- Handwheel: ring plus four spokes that cross it, the plain read of "vault".
    $ringR = if ($tiny) { $f * 0.30 } else { $f * 0.275 }
    $ringW = if ($tiny) { $f * 0.115 } else { $f * 0.085 }
    $ringPen = New-Object System.Drawing.Pen($accent, [single]$ringW)
    $g.DrawEllipse($ringPen, [single]($cx - $ringR), [single]($cy - $ringR), [single]($ringR * 2), [single]($ringR * 2))
    $ringPen.Dispose()

    if (-not $tiny) {
        $spokePen = New-Object System.Drawing.Pen($accent, [single]($f * 0.075))
        $spokePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $spokePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        for ($i = 0; $i -lt 4; $i++) {
            $a = [Math]::PI / 4 + [Math]::PI / 2 * $i
            $r1 = $f * 0.05
            $r2 = $f * 0.305
            $g.DrawLine($spokePen,
                [single]($cx + [Math]::Cos($a) * $r1), [single]($cy + [Math]::Sin($a) * $r1),
                [single]($cx + [Math]::Cos($a) * $r2), [single]($cy + [Math]::Sin($a) * $r2))
        }
        $spokePen.Dispose()
    }

    # --- Hub: a pale centre so the wheel still reads as a wheel when the spokes blur at 16px.
    $hubR = if ($tiny) { $f * 0.085 } else { $f * 0.10 }
    $hubBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 226, 236, 252))
    $g.FillEllipse($hubBrush, [single]($cx - $hubR), [single]($cy - $hubR), [single]($hubR * 2), [single]($hubR * 2))
    $hubBrush.Dispose()

    $grad.Dispose()
    $body.Dispose()
    $g.Dispose()
    return $bmp
}

function Get-DibBytes([System.Drawing.Bitmap]$bmp) {
    $w = $bmp.Width; $h = $bmp.Height
    $rect = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $stride = $data.Stride
    $pixels = New-Object byte[] ($stride * $h)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixels, 0, $pixels.Length)
    $bmp.UnlockBits($data)

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    # BITMAPINFOHEADER: height is doubled because a DIB icon stores colour + AND mask.
    $bw.Write([int]40); $bw.Write([int]$w); $bw.Write([int]($h * 2))
    $bw.Write([int16]1); $bw.Write([int16]32); $bw.Write([int]0)
    $bw.Write([int]($w * $h * 4)); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
    for ($y = $h - 1; $y -ge 0; $y--) { $bw.Write($pixels, $y * $stride, $w * 4) }
    # AND mask: fully zero, because the 32-bit alpha channel already carries transparency.
    $maskRow = [int]([Math]::Floor(($w + 31) / 32) * 4)
    $bw.Write((New-Object byte[] ($maskRow * $h)), 0, $maskRow * $h)
    $bw.Flush()
    $bytes = $ms.ToArray()
    $bw.Dispose(); $ms.Dispose()
    return , $bytes
}

function Get-PngBytes([System.Drawing.Bitmap]$bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $ms.ToArray()
    $ms.Dispose()
    return , $bytes
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
$entries = @()
foreach ($s in $sizes) {
    $bmp = New-IconBitmap $s
    # Large frames are PNG-compressed (a 256px DIB alone would be 256KB); the small ones stay
    # classic DIB, which is the form every shell surface reads without question.
    if ($s -ge 96) { $bytes = Get-PngBytes $bmp } else { $bytes = Get-DibBytes $bmp }
    $entries += [pscustomobject]@{ Size = $s; Bytes = $bytes }
    if ($s -eq 256 -or $s -eq 48 -or $s -eq 32 -or $s -eq 16) {
        $bmp.Save("$env:SCRATCH\preview-$s.png", [System.Drawing.Imaging.ImageFormat]::Png)
    }
    $bmp.Dispose()
}

$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($out)
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$entries.Count)
$offset = 6 + 16 * $entries.Count
foreach ($e in $entries) {
    $dim = if ($e.Size -ge 256) { 0 } else { $e.Size }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([int16]1); $w.Write([int16]32)
    $w.Write([int]$e.Bytes.Length); $w.Write([int]$offset)
    $offset += $e.Bytes.Length
}
foreach ($e in $entries) { $w.Write($e.Bytes, 0, $e.Bytes.Length) }
$w.Flush()
[System.IO.File]::WriteAllBytes($env:ICO_OUT, $out.ToArray())
$w.Dispose(); $out.Dispose()

Write-Output "wrote $env:ICO_OUT ($((Get-Item $env:ICO_OUT).Length) bytes, $($entries.Count) sizes)"
