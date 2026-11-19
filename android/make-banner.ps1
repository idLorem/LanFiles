Add-Type -AssemblyName System.Drawing
# TV banner 320x180: the same tile and computer glyph as LanFiles.ico, plus the name.
$font = if ((New-Object System.Drawing.Text.InstalledFontCollection).Families.Name -contains 'Segoe Fluent Icons') { 'Segoe Fluent Icons' } else { 'Segoe MDL2 Assets' }
$w = 320; $h = 180
$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'
$g.Clear([System.Drawing.Color]::FromArgb(0x20, 0x20, 0x20))
$d = 80; $x = 40; $y = ($h - $d) / 2; $r = $d * 0.24
$path = New-Object System.Drawing.Drawing2D.GraphicsPath
$path.AddArc($x, $y, $r, $r, 180, 90); $path.AddArc($x + $d - $r, $y, $r, $r, 270, 90)
$path.AddArc($x + $d - $r, $y + $d - $r, $r, $r, 0, 90); $path.AddArc($x, $y + $d - $r, $r, $r, 90, 90); $path.CloseFigure()
$g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0x19, 0x19, 0x19))), $path)
$g.DrawPath((New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(0x3A, 0x3A, 0x3A)), 1), $path)
$sf = New-Object System.Drawing.StringFormat; $sf.Alignment = 'Center'; $sf.LineAlignment = 'Center'
$g.DrawString([string][char]0xE977, (New-Object System.Drawing.Font $font, ($d * 0.58), ([System.Drawing.GraphicsUnit]::Pixel)), [System.Drawing.Brushes]::White, (New-Object System.Drawing.RectangleF $x, ($y + $d * 0.03), $d, $d), $sf)
$sf2 = New-Object System.Drawing.StringFormat; $sf2.LineAlignment = 'Center'
$g.DrawString('LanFiles', (New-Object System.Drawing.Font 'Segoe UI Semibold', 30, ([System.Drawing.GraphicsUnit]::Pixel)), [System.Drawing.Brushes]::White, (New-Object System.Drawing.RectangleF ($x + $d + 18), 0, ($w - $x - $d - 18), $h), $sf2)
$dir = Join-Path $PSScriptRoot 'res\drawable-xhdpi'; New-Item -ItemType Directory -Force $dir | Out-Null
$bmp.Save((Join-Path $dir 'banner.png'), [System.Drawing.Imaging.ImageFormat]::Png)
