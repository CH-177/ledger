# Generates src/Ledger.App/Assets/Ledger.ico: the dusty-blue "L" badge at every
# standard Windows icon size, each stored as PNG inside the .ico container.
param([string]$OutFile = (Join-Path $PSScriptRoot '..\src\Ledger.App\Assets\Ledger.ico'))

Add-Type -AssemblyName System.Drawing

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$accent = [System.Drawing.Color]::FromArgb(255, 0x52, 0x71, 0x8F)
$pngs = @()

foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.TextRenderingHint = 'AntiAliasGridFit'
    $g.Clear([System.Drawing.Color]::Transparent)

    # Rounded square, restrained radius (~22%).
    $r = [single]([math]::Max(2, $s * 0.22))
    $d = [single]($r * 2)
    $w = [single]($s - 1)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc([single]0, [single]0, $d, $d, 180, 90)
    $path.AddArc([single]($w - $d), [single]0, $d, $d, 270, 90)
    $path.AddArc([single]($w - $d), [single]($w - $d), $d, $d, 0, 90)
    $path.AddArc([single]0, [single]($w - $d), $d, $d, 90, 90)
    $path.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush($accent)), $path)

    # The "L", centred optically.
    $font = New-Object System.Drawing.Font('Segoe UI Variable Display', [single]($s * 0.56), [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $fmt = New-Object System.Drawing.StringFormat
    $fmt.Alignment = 'Center'; $fmt.LineAlignment = 'Center'
    $rect = New-Object System.Drawing.RectangleF([single]($s * 0.03), [single]($s * 0.02), [single]$s, [single]$s)
    $g.DrawString('L', $font, [System.Drawing.Brushes]::White, $rect, $fmt)
    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    if ($s -ge 256) {
        # Large frame as PNG (the Vista+ convention).
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    else {
        # Smaller frames as classic 32-bit DIBs for maximum compatibility:
        # BITMAPINFOHEADER (double height), bottom-up BGRA pixels, then a 1-bpp AND mask.
        $w = New-Object System.IO.BinaryWriter($ms)
        $w.Write([uint32]40); $w.Write([int32]$s); $w.Write([int32]($s * 2))
        $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]0)
        $w.Write([uint32]($s * $s * 4)); $w.Write([int32]0); $w.Write([int32]0); $w.Write([uint32]0); $w.Write([uint32]0)
        for ($y = $s - 1; $y -ge 0; $y--) {
            for ($x = 0; $x -lt $s; $x++) {
                $c = $bmp.GetPixel($x, $y)
                $w.Write([byte]$c.B); $w.Write([byte]$c.G); $w.Write([byte]$c.R); $w.Write([byte]$c.A)
            }
        }
        $maskRow = [int]([math]::Ceiling($s / 32.0) * 4)
        $w.Write((New-Object byte[] ($maskRow * $s)))   # all zero: alpha channel governs transparency
        $w.Flush()
    }
    $pngs += , $ms.ToArray()
    $bmp.Dispose()
}

# ICO container: ICONDIR + one ICONDIRENTRY per image, then the PNG payloads.
New-Item -ItemType Directory -Force (Split-Path $OutFile) | Out-Null
$fs = [System.IO.File]::Create($OutFile)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $pngs[$i].Length
    $bw.Write([byte]($s % 256)); $bw.Write([byte]($s % 256))   # 256 is written as 0
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$len); $bw.Write([uint32]$offset)
    $offset += $len
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Close()
Write-Output "Wrote $OutFile"
