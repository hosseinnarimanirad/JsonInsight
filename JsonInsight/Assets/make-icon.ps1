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

# One glyph as a path, scaled so its ink (not its font box) is $h tall and centred on ($cx,$cy).
# Measuring the ink is what keeps "{" and "}" the same height and optically centred at every size.
function Fill-Glyph($g, [string]$ch, [single]$cx, [single]$cy, [single]$h, $brush) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $ff = New-Object System.Drawing.FontFamily("Segoe UI Semibold")
    $p.AddString($ch, $ff, 0, [single]100, (New-Object System.Drawing.PointF(0, 0)),
        [System.Drawing.StringFormat]::GenericTypographic)
    $b = $p.GetBounds()
    $k = $h / $b.Height
    $m = New-Object System.Drawing.Drawing2D.Matrix
    $m.Translate($cx, $cy); $m.Scale($k, $k); $m.Translate(-($b.X + $b.Width / 2), -($b.Y + $b.Height / 2))
    $p.Transform($m)
    $g.FillPath($brush, $p)
    $m.Dispose(); $ff.Dispose(); $p.Dispose()
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

    # --- Tile: rounded square in the app's header navy, lit from the top-left.
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

    # Below 24px a thin stroke turns to grey mush, so the braces grow and the lens gets a
    # heavier ring rather than shrinking in proportion.
    $tiny = $S -lt 24

    # --- Braces: pale "{ }", the plain read of "JSON".
    $braceH = if ($tiny) { $f * 0.66 } else { $f * 0.56 }
    $braceDx = if ($tiny) { $f * 0.30 } else { $f * 0.29 }
    $paleBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 226, 236, 252))
    Fill-Glyph $g '{' ($cx - $braceDx) $cy $braceH $paleBrush
    Fill-Glyph $g '}' ($cx + $braceDx) $cy $braceH $paleBrush
    $paleBrush.Dispose()

    # --- Magnifier between them, in the accent: "insight into" the document. The lens sits a
    # little up and left so lens + handle together are centred, not the lens alone.
    $lensR = if ($tiny) { $f * 0.13 } else { $f * 0.115 }
    $lensW = if ($tiny) { $f * 0.09 } else { $f * 0.065 }
    $lx = $cx - $f * 0.03
    $ly = $cy - $f * 0.03
    $lensPen = New-Object System.Drawing.Pen($accent, [single]$lensW)
    $g.DrawEllipse($lensPen, [single]($lx - $lensR), [single]($ly - $lensR), [single](2 * $lensR), [single](2 * $lensR))
    $lensPen.Dispose()

    $handlePen = New-Object System.Drawing.Pen($accent, [single]($lensW * 1.25))
    $handlePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $handlePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $a = [Math]::PI / 4
    $h1 = $lensR + $lensW * 0.4
    $h2 = $lensR + $f * 0.12
    $g.DrawLine($handlePen,
        [single]($lx + [Math]::Cos($a) * $h1), [single]($ly + [Math]::Sin($a) * $h1),
        [single]($lx + [Math]::Cos($a) * $h2), [single]($ly + [Math]::Sin($a) * $h2))
    $handlePen.Dispose()

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
