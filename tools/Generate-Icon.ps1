<#
.SYNOPSIS
    生成 ResumePoint 的应用图标（多尺寸 .ico）。

.DESCRIPTION
    用 GDI+ 矢量绘制，不依赖任何外部素材：
      圆角方形 + 靛蓝→天青渐变底 + 白色顺时针回转箭头（⟳，"续播/回到断点"的语义）

    输出：
      src\ResumePoint\ResumePoint.ico    （应用图标，托盘/资源管理器/任务栏/安装包共用）
      src\ResumePoint\ResumePoint.png    （256px 预览，便于肉眼检查）

    用法：
      pwsh tools\Generate-Icon.ps1
#>

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$icoPath = Join-Path $root "src\ResumePoint\ResumePoint.ico"
$pngPath = Join-Path $root "src\ResumePoint\ResumePoint.png"

# ---------------------------------------------------------------- 绘制
function New-MasterBitmap([int]$size) {
    $bmp = [System.Drawing.Bitmap]::new($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = $size / 256.0

    # --- 圆角方形底 ---
    $radius = 56 * $s
    $inset = 8 * $s
    $rect = [System.Drawing.RectangleF]::new($inset, $inset, ($size - 2 * $inset), ($size - 2 * $inset))

    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $d = $radius * 2
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    $grad = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        $rect,
        [System.Drawing.Color]::FromArgb(255, 99, 102, 241),
        [System.Drawing.Color]::FromArgb(255, 14, 165, 233),
        45.0)
    $g.FillPath($grad, $path)

    # 顶部一层高光，让底色不那么平。
    # 注意：LinearGradientBrush 的渐变线长度 = |w·sinθ| + |h·cosθ|，
    # 若矩形不是正方形，渐变会溢出边界（导致在矩形下沿出现硬接缝），
    # 所以这里让高光覆盖整个矩形，渐变线才刚好等于高度。
    $gloss = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        $rect,
        [System.Drawing.Color]::FromArgb(48, 255, 255, 255),
        [System.Drawing.Color]::FromArgb(0, 255, 255, 255),
        90.0)
    $g.FillPath($gloss, $path)

    # --- 白色回转箭头 ⟳ ---
    $cx = $size / 2.0
    $cy = $size / 2.0
    $r = 74 * $s
    $stroke = 21 * $s

    $pen = [System.Drawing.Pen]::new([System.Drawing.Color]::White, $stroke)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round

    # 圆弧：从 -30°（1 点钟方向）顺时针扫 300°，末端落在 270°（12 点方向），右上角留缺口
    $arcBox = [System.Drawing.RectangleF]::new(($cx - $r), ($cy - $r), ($r * 2), ($r * 2))
    $g.DrawArc($pen, $arcBox, -30, 300)

    # 箭头：末端 270° 处，切线方向朝右（顺时针）
    $endAngle = 270.0
    $rad = $endAngle * [Math]::PI / 180
    $px = $cx + $r * [Math]::Cos($rad)
    $py = $cy + $r * [Math]::Sin($rad)
    $tx = -[Math]::Sin($rad)   # 顺时针切线
    $ty = [Math]::Cos($rad)
    $nx = -$ty                 # 法线
    $ny = $tx

    $tipLen = 40 * $s
    $halfW = 25 * $s
    $tipX = $px + $tx * $tipLen
    $tipY = $py + $ty * $tipLen

    $pts = [System.Drawing.PointF[]]::new(3)
    $pts[0] = [System.Drawing.PointF]::new($tipX, $tipY)
    $pts[1] = [System.Drawing.PointF]::new(($px + $nx * $halfW), ($py + $ny * $halfW))
    $pts[2] = [System.Drawing.PointF]::new(($px - $nx * $halfW), ($py - $ny * $halfW))

    $arrow = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $arrow.AddPolygon($pts)
    $g.FillPath([System.Drawing.SolidBrush]::new([System.Drawing.Color]::White), $arrow)

    $g.Dispose()
    return $bmp
}

# ---------------------------------------------------------------- ICO 组装
function Get-BmpEntry([System.Drawing.Bitmap]$bmp) {
    $w = $bmp.Width; $h = $bmp.Height
    $rect = [System.Drawing.Rectangle]::new(0, 0, $w, $h)
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
                          [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $bytes = [byte[]]::new($data.Stride * $h)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
    $bmp.UnlockBits($data)

    # .NET 的 Format32bppArgb 内存布局就是 BGRA —— 正是 ICO BMP 需要的顺序
    # ICO 的 BMP 是自下而上
    $rowSize = $w * 4
    $xor = [byte[]]::new($rowSize * $h)
    for ($y = 0; $y -lt $h; $y++) {
        [Array]::Copy($bytes, ($h - 1 - $y) * $rowSize, $xor, $y * $rowSize, $rowSize)
    }

    # AND 掩码（全 0 = 全部不透明，透明度交给 alpha 通道）
    $maskStride = [int]([Math]::Ceiling($w / 32.0) * 4)
    $mask = [byte[]]::new($maskStride * $h)

    $info = [byte[]]::new(40)
    # BITMAPINFOHEADER 字段偏移（写错会导致 GDI+ 报"操作成功完成"这种迷惑错误）：
    #   0  biSize(4) | 4 biWidth(4) | 8 biHeight(4) | 12 biPlanes(2)
    #   14 biBitCount(2) | 16 biCompression(4) | 20 biSizeImage(4) | 24..39 保留
    [Array]::Copy([System.BitConverter]::GetBytes([int]40), 0, $info, 0, 4)      # biSize
    [Array]::Copy([System.BitConverter]::GetBytes([int]$w), 0, $info, 4, 4)       # biWidth
    [Array]::Copy([System.BitConverter]::GetBytes([int]($h * 2)), 0, $info, 8, 4) # biHeight(含掩码)
    [Array]::Copy([System.BitConverter]::GetBytes([int]1), 0, $info, 12, 2)      # biPlanes
    [Array]::Copy([System.BitConverter]::GetBytes([int]32), 0, $info, 14, 2)     # biBitCount
    [Array]::Copy([System.BitConverter]::GetBytes([int]0), 0, $info, 16, 4)      # biCompression
    [Array]::Copy([System.BitConverter]::GetBytes([int]($xor.Length + $mask.Length)), 0, $info, 20, 4) # biSizeImage

    $entry = [byte[]]::new($info.Length + $xor.Length + $mask.Length)
    [Array]::Copy($info, 0, $entry, 0, $info.Length)
    [Array]::Copy($xor, 0, $entry, $info.Length, $xor.Length)
    [Array]::Copy($mask, 0, $entry, $info.Length + $xor.Length, $mask.Length)
    return $entry
}

# ---------------------------------------------------------------- 主流程
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$master = New-MasterBitmap 256

# PNG 预览（256px）
$master.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)
Write-Host "PNG 预览: $pngPath"

$images = @()
foreach ($s in $sizes) {
    $bmp = [System.Drawing.Bitmap]::new($s, $s)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($master, [System.Drawing.Rectangle]::new(0, 0, $s, $s))
    $g.Dispose()
    $images += [pscustomobject]@{ Size = $s; Data = (Get-BmpEntry $bmp) }
    $bmp.Dispose()
}
$master.Dispose()

# ICONDIR
$dir = [byte[]]::new(6)
[Array]::Copy([System.BitConverter]::GetBytes([int]0), 0, $dir, 0, 2)          # reserved
[Array]::Copy([System.BitConverter]::GetBytes([int]1), 0, $dir, 2, 2)          # type = icon
[Array]::Copy([System.BitConverter]::GetBytes([int]$images.Count), 0, $dir, 4, 2)

$entries = [byte[]]::new($images.Count * 16)
$offset = 6 + $entries.Length
$idx = 0
foreach ($img in $images) {
    $e = [byte[]]::new(16)
    $w = if ($img.Size -ge 256) { 0 } else { $img.Size }
    $h = if ($img.Size -ge 256) { 0 } else { $img.Size }
    $e[0] = [byte]$w
    $e[1] = [byte]$h
    [Array]::Copy([System.BitConverter]::GetBytes([int]1), 0, $e, 4, 2)      # planes
    [Array]::Copy([System.BitConverter]::GetBytes([int]32), 0, $e, 6, 2)     # bitcount
    [Array]::Copy([System.BitConverter]::GetBytes([int]$img.Data.Length), 0, $e, 8, 4)
    [Array]::Copy([System.BitConverter]::GetBytes([int]$offset), 0, $e, 12, 4)
    [Array]::Copy($e, 0, $entries, $idx * 16, 16)
    $offset += $img.Data.Length
    $idx++
}

$ms = [System.IO.MemoryStream]::new()
$ms.Write($dir, 0, $dir.Length)
$ms.Write($entries, 0, $entries.Length)
foreach ($img in $images) { $ms.Write($img.Data, 0, $img.Data.Length) }
[System.IO.File]::WriteAllBytes($icoPath, $ms.ToArray())
$ms.Dispose()

Write-Host "ICO 文件 : $icoPath  ($([Math]::Round((Get-Item $icoPath).Length / 1KB, 1)) KB, $($sizes -join '/') px)"
