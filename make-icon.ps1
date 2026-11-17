Add-Type -AssemblyName System.Drawing
$fontName = if ((New-Object System.Drawing.Text.InstalledFontCollection).Families.Name -contains 'Segoe Fluent Icons') { 'Segoe Fluent Icons' } else { 'Segoe MDL2 Assets' }
$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$frames = foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'
    $pad = [Math]::Max(0, [int]($s * 0.04)); $d = $s - 2 * $pad; $r = [Math]::Max(2, $d * 0.24)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($pad, $pad, $r, $r, 180, 90); $path.AddArc($pad + $d - $r, $pad, $r, $r, 270, 90)
    $path.AddArc($pad + $d - $r, $pad + $d - $r, $r, $r, 0, 90); $path.AddArc($pad, $pad + $d - $r, $r, $r, 90, 90); $path.CloseFigure()
    $rect = New-Object System.Drawing.RectangleF $pad, $pad, $d, $d
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0x19, 0x19, 0x19))), $path)
    $font = New-Object System.Drawing.Font $fontName, ([float]($s * 0.58)), ([System.Drawing.GraphicsUnit]::Pixel)
    $sf = New-Object System.Drawing.StringFormat; $sf.Alignment = 'Center'; $sf.LineAlignment = 'Center'
    $g.DrawString([string][char]0xE977, $font, [System.Drawing.Brushes]::White, (New-Object System.Drawing.RectangleF 0, ($s * 0.03), $s, $s), $sf)
    $ms = New-Object System.IO.MemoryStream; $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    , @($s, $ms.ToArray())
}
$out = New-Object System.IO.MemoryStream; $w = New-Object System.IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $s = $f[0]; $b = $f[1]; $sz = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$sz); $w.Write([byte]$sz); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$b.Length); $w.Write([uint32]$offset)
    $offset += $b.Length
}
foreach ($f in $frames) { $w.Write([byte[]]$f[1]) }
[System.IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'LanFiles.ico'), $out.ToArray())
