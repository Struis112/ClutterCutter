# Build ClutterCutter.ico from ClutterCutter.png with multiple resolutions.
# Each entry stores the image as PNG (Vista+ format) — keeps the file small and preserves alpha.

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$srcPath = Join-Path $PSScriptRoot 'ClutterCutter.png'
$icoPath = Join-Path $PSScriptRoot 'ClutterCutter.ico'

if (-not (Test-Path $srcPath)) { throw "Source PNG not found: $srcPath" }

$src = [System.Drawing.Image]::FromFile($srcPath)
try {
  $sizes = @(256, 128, 64, 48, 32, 24, 16)
  $entries = New-Object 'System.Collections.Generic.List[byte[]]'
  $infos   = New-Object 'System.Collections.Generic.List[hashtable]'

  foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    try {
      $g = [System.Drawing.Graphics]::FromImage($bmp)
      try {
        $g.InterpolationMode  = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.SmoothingMode      = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $g.PixelOffsetMode    = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $g.Clear([System.Drawing.Color]::Transparent)
        $g.DrawImage($src, 0, 0, $s, $s)
      } finally { $g.Dispose() }
      $ms = New-Object System.IO.MemoryStream
      $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
      $bytes = $ms.ToArray()
      $entries.Add($bytes) | Out-Null
      $infos.Add(@{ Size = $s; Length = $bytes.Length }) | Out-Null
      $ms.Dispose()
    } finally { $bmp.Dispose() }
  }

  # Compute offsets. Header is 6 bytes + (16 bytes per directory entry).
  $headerSize = 6 + (16 * $entries.Count)
  $offsets = New-Object 'int[]' $entries.Count
  $cursor = $headerSize
  for ($i = 0; $i -lt $entries.Count; $i++) {
    $offsets[$i] = $cursor
    $cursor += $entries[$i].Length
  }

  $fs = [System.IO.File]::Open($icoPath, [System.IO.FileMode]::Create)
  try {
    $bw = New-Object System.IO.BinaryWriter $fs
    # ICONDIR
    $bw.Write([UInt16]0)              # Reserved
    $bw.Write([UInt16]1)              # Type 1 = icon
    $bw.Write([UInt16]$entries.Count) # Count

    # ICONDIRENTRY for each
    for ($i = 0; $i -lt $entries.Count; $i++) {
      $sz = $infos[$i].Size
      $width  = if ($sz -ge 256) { [byte]0 } else { [byte]$sz }
      $height = if ($sz -ge 256) { [byte]0 } else { [byte]$sz }
      $bw.Write($width)                         # Width  (0 = 256)
      $bw.Write($height)                        # Height (0 = 256)
      $bw.Write([byte]0)                        # ColorCount
      $bw.Write([byte]0)                        # Reserved
      $bw.Write([UInt16]1)                      # ColorPlanes
      $bw.Write([UInt16]32)                     # BitsPerPixel
      $bw.Write([UInt32]$infos[$i].Length)      # bytesInRes
      $bw.Write([UInt32]$offsets[$i])           # imageOffset
    }
    # Image data
    for ($i = 0; $i -lt $entries.Count; $i++) { $bw.Write($entries[$i]) }
    $bw.Flush()
  } finally { $fs.Dispose() }
} finally { $src.Dispose() }

$icoLen = (Get-Item $icoPath).Length
Write-Host "Wrote $icoPath ($icoLen bytes)"
