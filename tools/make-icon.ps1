# Génère l'icône de l'application (assets\icon.ico, PNG multi-tailles) : trois barres menthe sur fond nuit, comme la marque de l'interface.
# Usage : powershell -File tools\make-icon.ps1
Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot -Parent
$assets = Join-Path $root 'assets'
New-Item -ItemType Directory -Force -Path $assets | Out-Null

function New-RoundedPath([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
  $p = New-Object System.Drawing.Drawing2D.GraphicsPath
  $d = [single]($r * 2)
  $p.AddArc($x, $y, $d, $d, 180, 90)
  $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
  $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
  $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
  $p.CloseFigure()
  return $p
}

function New-IconBitmap([int]$size) {
  $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.Clear([System.Drawing.Color]::Transparent)
  $s = $size / 256.0

  # Fond : carré arrondi, dégradé nuit
  $bg = New-RoundedPath (6*$s) (6*$s) (244*$s) (244*$s) (58*$s)
  $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush((New-Object System.Drawing.PointF(0,0)), (New-Object System.Drawing.PointF(0,[single]$size)), [System.Drawing.Color]::FromArgb(255,24,38,56), [System.Drawing.Color]::FromArgb(255,8,13,21))
  $g.FillPath($brush, $bg)
  if ($size -ge 32) {
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(70,104,229,190), [single]([math]::Max(1, 3*$s)))
    $g.DrawPath($pen, $bg); $pen.Dispose()
  }

  # Trois barres menthe (mêmes proportions que la marque de l'interface : 8 / 14 / 11)
  $barW = 40*$s; $gap = 22*$s
  $total = 3*$barW + 2*$gap
  $x0 = (256*$s - $total)/2
  $base = 190*$s
  $heights = @((84*$s), (148*$s), (116*$s))
  $alphas = @(190, 255, 225)
  for ($i = 0; $i -lt 3; $i++) {
    $x = $x0 + $i*($barW + $gap)
    $h = $heights[$i]
    if ($size -ge 48) {
      # halo doux
      $halo = New-RoundedPath ($x - 7*$s) ($base - $h - 7*$s) ($barW + 14*$s) ($h + 14*$s) ($barW/2 + 7*$s)
      $hb = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(38,104,229,190))
      $g.FillPath($hb, $halo); $hb.Dispose(); $halo.Dispose()
    }
    $bar = New-RoundedPath $x ($base - $h) $barW $h ($barW/2)
    $rect = New-Object System.Drawing.RectangleF([single]$x, [single]($base - $h), [single]$barW, [single]$h)
    $bb = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, [System.Drawing.Color]::FromArgb($alphas[$i],150,255,225), [System.Drawing.Color]::FromArgb($alphas[$i],63,184,150), 90)
    $g.FillPath($bb, $bar); $bb.Dispose(); $bar.Dispose()
  }
  # Petit point d'accent (le « pouls »)
  if ($size -ge 32) {
    $dot = 16*$s
    $db = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255,246,199,110))
    $g.FillEllipse($db, [single](256*$s - 70*$s), [single](50*$s), [single]$dot, [single]$dot); $db.Dispose()
  }
  $brush.Dispose(); $bg.Dispose(); $g.Dispose()
  return $bmp
}

# Entrée « bitmap » (DIB 32 bits + masque) : lue correctement par toutes les versions de Windows et de .NET.
function ConvertTo-DibIcon($bmp) {
  $w = $bmp.Width; $h = $bmp.Height
  $rect = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
  $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $bytes = New-Object byte[] ($data.Stride * $h)
  [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
  $bmp.UnlockBits($data)
  $maskStride = [int]([math]::Floor(($w + 31) / 32) * 4)
  $out = New-Object System.IO.MemoryStream
  $bw = New-Object System.IO.BinaryWriter($out)
  $bw.Write([uint32]40); $bw.Write([int32]$w); $bw.Write([int32]($h * 2)); $bw.Write([uint16]1); $bw.Write([uint16]32)
  $bw.Write([uint32]0); $bw.Write([uint32]($w * $h * 4 + $maskStride * $h)); $bw.Write([int32]0); $bw.Write([int32]0); $bw.Write([uint32]0); $bw.Write([uint32]0)
  for ($y = $h - 1; $y -ge 0; $y--) { $bw.Write($bytes, $y * $data.Stride, $w * 4) }   # lignes de bas en haut (BGRA)
  $bw.Write((New-Object byte[] ($maskStride * $h)))                                     # masque AND : tout opaque, la transparence vient de l'alpha
  $bw.Flush(); $res = $out.ToArray(); $bw.Dispose(); $out.Dispose()
  return ,$res
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = @{}
foreach ($sz in $sizes) {
  $bmp = New-IconBitmap $sz
  $ms = New-Object System.IO.MemoryStream
  if ($sz -ge 256) { $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $pngs[$sz] = $ms.ToArray() }
  else { $pngs[$sz] = (ConvertTo-DibIcon $bmp) }
  if ($sz -in 256, 64) { $bmp.Save((Join-Path $assets "icon-$sz.png"), [System.Drawing.Imaging.ImageFormat]::Png) }
  $bmp.Dispose(); $ms.Dispose()
}

# Fichier .ico (entrées PNG, pris en charge par Windows Vista et suivants)
$ico = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($ico)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
foreach ($sz in $sizes) {
  $data = $pngs[$sz]
  $dim = if ($sz -ge 256) { 0 } else { $sz }
  $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
  $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$data.Length); $w.Write([uint32]$offset)
  $offset += $data.Length
}
foreach ($sz in $sizes) { $w.Write($pngs[$sz]) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $assets 'icon.ico'), $ico.ToArray())
$w.Dispose(); $ico.Dispose()
"Icône créée : " + (Join-Path $assets 'icon.ico') + " (" + [math]::Round((Get-Item (Join-Path $assets 'icon.ico')).Length/1KB) + " Ko, tailles : " + ($sizes -join ', ') + ")"
