# Generates whale.ico (a DeepSeek-style blue whale) for the launcher exe.
# Renders a 1024px master, downscales to 16/24/32/48/64/128/256, packs PNG entries into an .ico.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$outDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$icoPath = Join-Path $outDir 'whale.ico'

function New-WhaleMaster {
    $bmp = New-Object System.Drawing.Bitmap 1024, 1024, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

    $blue   = [System.Drawing.Color]::FromArgb(255, 77, 107, 254)   # DeepSeek blue #4D6BFE
    $dark   = [System.Drawing.Color]::FromArgb(255, 47, 66, 190)    # darker outline/shade
    $belly  = [System.Drawing.Color]::FromArgb(255, 214, 225, 255)  # light belly
    $spout  = [System.Drawing.Color]::FromArgb(255, 148, 168, 255)  # water spout
    $drop   = [System.Drawing.Color]::FromArgb(255, 120, 143, 255)  # droplets

    $penBody  = New-Object System.Drawing.Pen $blue, 14
    $penShade = New-Object System.Drawing.Pen $dark, 14
    $penSpout = New-Object System.Drawing.Pen $spout, 20
    $penSpout.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $penSpout.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $penShade.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $penShade.EndCap = [System.Drawing.Drawing2D.LineCap]::Round

    # --- whale body (facing left), torpedo shape with rounded head ---
    $body = New-Object System.Drawing.Drawing2D.GraphicsPath
    $body.StartFigure()
    $body.AddBezier(60, 520, 150, 420, 300, 368, 470, 372)      # head -> top of back
    $body.AddBezier(470, 372, 640, 376, 800, 400, 872, 470)     # back -> tail base top
    $body.AddBezier(872, 470, 1012, 396, 1008, 560, 882, 540)   # tail upper fluke out
    $body.AddBezier(882, 540, 950, 560, 936, 640, 852, 596)     # fluke notch
    $body.AddBezier(852, 596, 930, 648, 900, 742, 830, 668)     # lower fluke in
    $body.AddBezier(830, 668, 806, 628, 780, 606, 730, 606)     # to tail base bottom
    $body.AddBezier(730, 606, 620, 700, 420, 744, 230, 706)     # belly -> bottom of head
    $body.AddBezier(230, 706, 150, 680, 80, 610, 60, 520)       # chin -> nose
    $body.CloseFigure()

    $fillBlue = New-Object System.Drawing.SolidBrush $blue
    $g.FillPath($fillBlue, $body)
    $g.DrawPath($penShade, $body)

    # --- belly highlight (lighter region along the bottom) ---
    $bellyPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $bellyPath.StartFigure()
    $bellyPath.AddBezier(150, 636, 300, 700, 470, 714, 640, 688)
    $bellyPath.AddBezier(640, 688, 600, 640, 540, 610, 470, 604)
    $bellyPath.AddBezier(470, 604, 380, 640, 260, 640, 180, 606)
    $bellyPath.CloseFigure()
    $fillBelly = New-Object System.Drawing.SolidBrush $belly
    $g.FillPath($fillBelly, $bellyPath)

    # --- flipper ---
    $fin = New-Object System.Drawing.Drawing2D.GraphicsPath
    $fin.StartFigure()
    $fin.AddBezier(430, 596, 470, 700, 420, 742, 372, 730)   # upper edge down-forward
    $fin.AddBezier(372, 730, 400, 690, 404, 640, 430, 596)   # back up
    $fin.CloseFigure()
    $g.FillPath($fillBlue, $fin)
    $g.DrawPath($penShade, $fin)

    # --- water spout above the head ---
    $g.DrawBezier($penSpout, 168, 428, 120, 300, 170, 210, 250, 168)   # main spout up-right
    $g.DrawBezier($penSpout, 268, 404, 240, 300, 290, 230, 360, 196)   # second spout
    # droplets
    $g.FillEllipse((New-Object System.Drawing.SolidBrush $drop), 226, 118, 44, 44)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush $drop), 306, 96, 36, 36)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush $drop), 372, 140, 30, 30)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush $drop), 166, 152, 30, 30)

    # --- eye ---
    $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)), 108, 428, 46, 52)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 24, 34, 92))), 122, 440, 24, 28)

    $g.Dispose()
    return $bmp
}

$master = New-WhaleMaster
$master.Save((Join-Path $outDir 'whale-preview.png'), [System.Drawing.Imaging.ImageFormat]::Png)

# Downscale to each icon size
$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = @{}
foreach ($s in $sizes) {
    $small = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g2 = [System.Drawing.Graphics]::FromImage($small)
    $g2.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g2.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g2.DrawImage($master, 0, 0, $s, $s)
    $g2.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $small.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs[$s] = $ms.ToArray()
    $ms.Dispose()
    $small.Dispose()
}
$master.Dispose()

# Pack ICO (PNG-compressed entries)
$fs = [System.IO.File]::Create($icoPath)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
foreach ($s in $sizes) {
    $data = $pngs[$s]
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$data.Length); $bw.Write([uint32]$offset)
    $offset += $data.Length
}
foreach ($s in $sizes) { $bw.Write($pngs[$s]) }
$bw.Flush(); $bw.Close(); $fs.Close()

Write-Host "whale.ico written: $((Get-Item $icoPath).Length) bytes"
