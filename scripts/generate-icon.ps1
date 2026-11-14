param(
    [switch]$Force
)
$ErrorActionPreference = 'Stop'

function New-RoundedRectPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = [Math]::Min([Math]::Min($r * 2.0, $w), $h)
    if ($d -le 0.5) {
        $path.AddRectangle((New-Object System.Drawing.RectangleF($x, $y, $w, $h)))
        return $path
    }
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-WinScrcpyBitmap([int]$targetSize) {
    $scale = 4
    $canvasSize = $targetSize * $scale
    $master = New-Object System.Drawing.Bitmap($canvasSize, $canvasSize, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $g = [System.Drawing.Graphics]::FromImage($master)
        try {
            $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
            $g.Clear([System.Drawing.Color]::Transparent)

            $s = [float]$canvasSize

            # --- Base Layer: Unified Squircle Silhouette ---
            $pad = $s * 0.020
            $sqRect = New-Object System.Drawing.RectangleF($pad, $pad, ($s - 2.0 * $pad), ($s - 2.0 * $pad))
            $sqPath = New-RoundedRectPath $sqRect.X $sqRect.Y $sqRect.Width $sqRect.Height ($s * 0.235)
            try {
                $sqBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
                    (New-Object System.Drawing.PointF($sqRect.X, $sqRect.Y)),
                    (New-Object System.Drawing.PointF($sqRect.Right, $sqRect.Bottom)),
                    [System.Drawing.Color]::FromArgb(255, 56, 189, 248),
                    [System.Drawing.Color]::FromArgb(255, 29, 78, 216)
                )
                $sqBlend = New-Object System.Drawing.Drawing2D.ColorBlend(3)
                $sqBlend.Colors = [System.Drawing.Color[]]@(
                    [System.Drawing.Color]::FromArgb(255, 56, 189, 248),
                    [System.Drawing.Color]::FromArgb(255, 0, 122, 255),
                    [System.Drawing.Color]::FromArgb(255, 29, 78, 216)
                )
                $sqBlend.Positions = [float[]]@(0.0, 0.48, 1.0)
                $sqBrush.InterpolationColors = $sqBlend
                try {
                    $g.FillPath($sqBrush, $sqPath)
                } finally {
                    $sqBrush.Dispose()
                }

                # Soft ambient inner glow inside squircle
                $sqState = $g.Save()
                $g.SetClip($sqPath)
                try {
                    $glowBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(48, 255, 255, 255))
                    try {
                        $g.FillEllipse($glowBrush, ($s * 0.04), (-$s * 0.22), ($s * 0.88), ($s * 0.72))
                    } finally {
                        $glowBrush.Dispose()
                    }

                    # --- Layer 2: Frosted Glass Desktop Window Card (Back Left) ---
                    $winX = $s * 0.135
                    $winY = $s * 0.175
                    $winW = $s * 0.56
                    $winH = $s * 0.42
                    $winR = $s * 0.068

                    # Soft shadow under desktop card
                    for ($sp = 2; $sp -ge 1; $sp--) {
                        $off = $s * 0.010 * $sp
                        $wShPath = New-RoundedRectPath ($winX - $off * 0.2) ($winY + $off * 0.9) ($winW + $off * 0.4) ($winH + $off * 0.4) $winR
                        $wShBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb((26 - $sp * 8), 10, 32, 82))
                        try {
                            $g.FillPath($wShBrush, $wShPath)
                        } finally {
                            $wShBrush.Dispose()
                            $wShPath.Dispose()
                        }
                    }

                    $winPath = New-RoundedRectPath $winX $winY $winW $winH $winR
                    try {
                        $winBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
                            (New-Object System.Drawing.PointF($winX, $winY)),
                            (New-Object System.Drawing.PointF(($winX + $winW), ($winY + $winH))),
                            [System.Drawing.Color]::FromArgb(236, 248, 252, 255),
                            [System.Drawing.Color]::FromArgb(215, 214, 236, 255)
                        )
                        try {
                            $g.FillPath($winBrush, $winPath)
                        } finally {
                            $winBrush.Dispose()
                        }

                        $winState = $g.Save()
                        $g.SetClip($winPath)
                        try {
                            # Subtle window titlebar band + hairline divider
                            $barH = $s * 0.085
                            $barBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(245, 255, 255, 255))
                            $divPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(45, 29, 78, 216), [Math]::Max(1.0, $s * 0.010))
                            try {
                                $g.FillRectangle($barBrush, $winX, $winY, $winW, $barH)
                                $g.DrawLine($divPen, $winX, ($winY + $barH), ($winX + $winW), ($winY + $barH))
                            } finally {
                                $barBrush.Dispose()
                                $divPen.Dispose()
                            }

                            # Subtle azure wireless stream arcs inside frosted desktop viewport
                            $waveCx = $s * 0.45
                            $waveCy = $s * 0.47
                            $wavePenW = [Math]::Max(1.6, $s * 0.028)
                            foreach ($wSpec in @(
                                @{ r = 0.115; a = 215 },
                                @{ r = 0.195; a = 145 }
                            )) {
                                $wr = $s * $wSpec.r
                                $wPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb($wSpec.a, 0, 102, 224), $wavePenW)
                                $wPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
                                $wPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
                                try {
                                    $g.DrawArc($wPen, ($waveCx - $wr), ($waveCy - $wr), ($wr * 2.0), ($wr * 2.0), 186, 62)
                                } finally {
                                    $wPen.Dispose()
                                }
                            }
                        } finally {
                            $g.Restore($winState)
                        }

                        # Crisp white glass border on desktop window card
                        $winBorderPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(235, 255, 255, 255), [Math]::Max(1.2, $s * 0.013))
                        try {
                            $g.DrawPath($winBorderPen, $winPath)
                        } finally {
                            $winBorderPen.Dispose()
                        }
                    } finally {
                        $winPath.Dispose()
                    }

                    # --- Layer 3: Soft Drop Shadow cast by Smartphone ---
                    $phX = $s * 0.465
                    $phY = $s * 0.245
                    $phW = $s * 0.405
                    $phH = $s * 0.61
                    $phR = $s * 0.075

                    for ($sp = 4; $sp -ge 1; $sp--) {
                        $spread = $s * 0.011 * $sp
                        $shPath = New-RoundedRectPath ($phX - $s * 0.020 - $spread * 0.35) ($phY + $s * 0.020 - $spread * 0.1) ($phW + $spread * 0.9) ($phH + $spread * 0.85) ($phR + $spread * 0.35)
                        $shBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb((26 - $sp * 4), 10, 28, 74))
                        try {
                            $g.FillPath($shBrush, $shPath)
                        } finally {
                            $shBrush.Dispose()
                            $shPath.Dispose()
                        }
                    }

                    # --- Layer 4: Sleek Slate Smartphone + Emerald-Cyan Screen (Front Right) ---
                    $phPath = New-RoundedRectPath $phX $phY $phW $phH $phR
                    try {
                        $phBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
                            (New-Object System.Drawing.PointF($phX, $phY)),
                            (New-Object System.Drawing.PointF(($phX + $phW), ($phY + $phH))),
                            [System.Drawing.Color]::FromArgb(255, 51, 65, 85),
                            [System.Drawing.Color]::FromArgb(255, 15, 23, 42)
                        )
                        try {
                            $g.FillPath($phBrush, $phPath)
                        } finally {
                            $phBrush.Dispose()
                        }

                        # Crisp metallic inner highlight along smartphone body
                        $phRimPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(145, 255, 255, 255), [Math]::Max(1.2, $s * 0.013))
                        try {
                            $g.DrawPath($phRimPen, $phPath)
                        } finally {
                            $phRimPen.Dispose()
                        }

                        # Inner Emerald -> Cyan -> Blue screen
                        $bezel = $s * 0.034
                        $scrX = $phX + $bezel
                        $scrY = $phY + $bezel
                        $scrW = $phW - 2.0 * $bezel
                        $scrH = $phH - 2.0 * $bezel
                        $scrPath = New-RoundedRectPath $scrX $scrY $scrW $scrH ($s * 0.048)
                        try {
                            $scrBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
                                (New-Object System.Drawing.PointF($scrX, $scrY)),
                                (New-Object System.Drawing.PointF(($scrX + $scrW), ($scrY + $scrH))),
                                [System.Drawing.Color]::FromArgb(255, 16, 185, 129),
                                [System.Drawing.Color]::FromArgb(255, 2, 132, 199)
                            )
                            $scrBlend = New-Object System.Drawing.Drawing2D.ColorBlend(3)
                            $scrBlend.Colors = [System.Drawing.Color[]]@(
                                [System.Drawing.Color]::FromArgb(255, 16, 185, 129),
                                [System.Drawing.Color]::FromArgb(255, 6, 182, 212),
                                [System.Drawing.Color]::FromArgb(255, 2, 132, 199)
                            )
                            $scrBlend.Positions = [float[]]@(0.0, 0.50, 1.0)
                            $scrBrush.InterpolationColors = $scrBlend
                            try {
                                $g.FillPath($scrBrush, $scrPath)
                            } finally {
                                $scrBrush.Dispose()
                            }

                            # Top sheen on screen + sleek rounded play/mirror triangle
                            $scrState = $g.Save()
                            $g.SetClip($scrPath)
                            try {
                                $sheen = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(42, 255, 255, 255))
                                try {
                                    $g.FillEllipse($sheen, ($scrX - $scrW * 0.20), ($scrY - $scrH * 0.42), ($scrW * 1.40), ($scrH * 0.72))
                                } finally {
                                    $sheen.Dispose()
                                }

                                if ($targetSize -ge 40) {
                                    $pillW = $scrW * 0.34
                                    $pillH = [Math]::Max(1.4, $s * 0.015)
                                    $pillX = $scrX + ($scrW - $pillW) * 0.5
                                    $pillY = $scrY + $scrH - $s * 0.030
                                    $pillPath = New-RoundedRectPath $pillX $pillY $pillW $pillH ($pillH * 0.5)
                                    $pillBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(180, 255, 255, 255))
                                    try {
                                        $g.FillPath($pillBrush, $pillPath)
                                    } finally {
                                        $pillBrush.Dispose()
                                        $pillPath.Dispose()
                                    }
                                }
                            } finally {
                                $g.Restore($scrState)
                            }

                            # Centerpiece minimalist white rounded stream/play emblem
                            $cx = $scrX + $scrW * 0.525
                            $cy = $scrY + $scrH * 0.490
                            $pr = $s * 0.068
                            $triPts = [System.Drawing.PointF[]]@(
                                (New-Object System.Drawing.PointF(($cx - $pr * 0.58), ($cy - $pr * 0.85))),
                                (New-Object System.Drawing.PointF(($cx + $pr * 0.88), $cy)),
                                (New-Object System.Drawing.PointF(($cx - $pr * 0.58), ($cy + $pr * 0.85)))
                            )
                            $triShPts = [System.Drawing.PointF[]]@(
                                (New-Object System.Drawing.PointF(($triPts[0].X + $s * 0.008), ($triPts[0].Y + $s * 0.014))),
                                (New-Object System.Drawing.PointF(($triPts[1].X + $s * 0.008), ($triPts[1].Y + $s * 0.014))),
                                (New-Object System.Drawing.PointF(($triPts[2].X + $s * 0.008), ($triPts[2].Y + $s * 0.014)))
                            )
                            $roundJoinW = $s * 0.024
                            $triShPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(55, 5, 20, 45), $roundJoinW)
                            $triShPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
                            $triShBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(55, 5, 20, 45))
                            $triPen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, $roundJoinW)
                            $triPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
                            try {
                                $g.FillPolygon($triShBrush, $triShPts)
                                $g.DrawPolygon($triShPen, $triShPts)
                                $g.FillPolygon([System.Drawing.Brushes]::White, $triPts)
                                $g.DrawPolygon($triPen, $triPts)
                            } finally {
                                $triShPen.Dispose()
                                $triShBrush.Dispose()
                                $triPen.Dispose()
                            }
                        } finally {
                            $scrPath.Dispose()
                        }
                    } finally {
                        $phPath.Dispose()
                    }
                } finally {
                    $g.Restore($sqState)
                }

                # Crisp top-left specular border on squircle
                $sqRimPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(60, 255, 255, 255), [Math]::Max(1.2, $s * 0.013))
                try {
                    $g.DrawPath($sqRimPen, $sqPath)
                } finally {
                    $sqRimPen.Dispose()
                }
            } finally {
                $sqPath.Dispose()
            }
        } finally {
            $g.Dispose()
        }

        $outBmp = New-Object System.Drawing.Bitmap($targetSize, $targetSize, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $gOut = [System.Drawing.Graphics]::FromImage($outBmp)
        try {
            $gOut.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $gOut.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $gOut.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $gOut.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
            $gOut.DrawImage($master, 0, 0, $targetSize, $targetSize)
        } finally {
            $gOut.Dispose()
        }
        return $outBmp
    } finally {
        $master.Dispose()
    }
}

function ConvertTo-IcoDibFrame([System.Drawing.Bitmap]$bmp) {
    $w = $bmp.Width
    $h = $bmp.Height
    $rect = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
    $bmpData = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $stride = [Math]::Abs($bmpData.Stride)
        $raw = New-Object byte[] ($stride * $h)
        [System.Runtime.InteropServices.Marshal]::Copy($bmpData.Scan0, $raw, 0, $raw.Length)
    } finally {
        $bmp.UnlockBits($bmpData)
    }

    $andRowStride = ([int][Math]::Ceiling($w / 32.0)) * 4
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    try {
        # BITMAPINFOHEADER (40 bytes)
        $bw.Write([uint32]40)
        $bw.Write([int32]$w)
        $bw.Write([int32]($h * 2))
        $bw.Write([uint16]1)
        $bw.Write([uint16]32)
        $bw.Write([uint32]0)
        $bw.Write([uint32]($w * $h * 4))
        $bw.Write([int32]0)
        $bw.Write([int32]0)
        $bw.Write([uint32]0)
        $bw.Write([uint32]0)

        # Bottom-up BGRA XOR mask
        for ($y = $h - 1; $y -ge 0; $y--) {
            $rowOffset = $y * $stride
            $bw.Write($raw, $rowOffset, ($w * 4))
        }

        # Bottom-up 1bpp AND mask (bit=1 where alpha==0)
        for ($y = $h - 1; $y -ge 0; $y--) {
            $rowOffset = $y * $stride
            $maskRow = New-Object byte[] $andRowStride
            for ($x = 0; $x -lt $w; $x++) {
                $alpha = $raw[$rowOffset + ($x * 4) + 3]
                if ($alpha -eq 0) {
                    $byteIndex = [int][Math]::Floor($x / 8)
                    $bitMask = [byte](128 -shr ($x % 8))
                    $maskRow[$byteIndex] = [byte]($maskRow[$byteIndex] -bor $bitMask)
                }
            }
            $bw.Write($maskRow)
        }
        $bw.Flush()
        return $ms.ToArray()
    } finally {
        $bw.Dispose()
        $ms.Dispose()
    }
}

function ConvertTo-IcoPngFrame([System.Drawing.Bitmap]$bmp) {
    $ms = New-Object System.IO.MemoryStream
    try {
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        return $ms.ToArray()
    } finally {
        $ms.Dispose()
    }
}

function Ensure-AppIcon([string]$iconPath, [string]$pngPath) {
    $hasIco = Test-Path -LiteralPath $iconPath
    $hasPng = [string]::IsNullOrEmpty($pngPath) -or (Test-Path -LiteralPath $pngPath)
    if ($hasIco -and $hasPng) {
        return
    }
    Add-Type -AssemblyName System.Drawing
    if (-not $hasPng) {
        $pngDir = Split-Path -Parent $pngPath
        if ($pngDir -and -not (Test-Path -LiteralPath $pngDir)) {
            New-Item -ItemType Directory -Path $pngDir -Force | Out-Null
        }
        $bmp256 = New-WinScrcpyBitmap 256
        try {
            $bmp256.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)
        } finally {
            $bmp256.Dispose()
        }
    }
    if ($hasIco) {
        return
    }
    $sizes = @(16, 20, 24, 32, 40, 48, 64, 256)
    $frames = New-Object System.Collections.Generic.List[byte[]]
    foreach ($sz in $sizes) {
        $bmp = New-WinScrcpyBitmap $sz
        try {
            if ($sz -ge 256) {
                $frames.Add((ConvertTo-IcoPngFrame $bmp))
            } else {
                $frames.Add((ConvertTo-IcoDibFrame $bmp))
            }
        } finally {
            $bmp.Dispose()
        }
    }

    $icoDir = Split-Path -Parent $iconPath
    if ($icoDir -and -not (Test-Path -LiteralPath $icoDir)) {
        New-Item -ItemType Directory -Path $icoDir -Force | Out-Null
    }
    $fs = [System.IO.File]::Create($iconPath)
    $bw = New-Object System.IO.BinaryWriter($fs)
    try {
        # ICONDIR header
        $bw.Write([uint16]0)
        $bw.Write([uint16]1)
        $bw.Write([uint16]$sizes.Count)

        $offset = 6 + (16 * $sizes.Count)
        for ($i = 0; $i -lt $sizes.Count; $i++) {
            $sz = $sizes[$i]
            $bytes = $frames[$i]
            $dim = if ($sz -ge 256) { [byte]0 } else { [byte]$sz }
            $bw.Write($dim)
            $bw.Write($dim)
            $bw.Write([byte]0)
            $bw.Write([byte]0)
            $bw.Write([uint16]1)
            $bw.Write([uint16]32)
            $bw.Write([uint32]$bytes.Length)
            $bw.Write([uint32]$offset)
            $offset += $bytes.Length
        }

        for ($i = 0; $i -lt $frames.Count; $i++) {
            $bw.Write($frames[$i])
        }
    } finally {
        $bw.Dispose()
        $fs.Dispose()
    }
}
$rootDir = Split-Path -Parent $PSScriptRoot
$iconPath = Join-Path $rootDir 'assets\app.ico'
$pngIconPath = Join-Path $rootDir 'assets\icon.png'
if ($Force) {
    if (Test-Path -LiteralPath $iconPath) { Remove-Item -LiteralPath $iconPath -Force }
    if (Test-Path -LiteralPath $pngIconPath) { Remove-Item -LiteralPath $pngIconPath -Force }
}
Ensure-AppIcon $iconPath $pngIconPath
Write-Output "Generated icons: $iconPath, $pngIconPath"
