param(
    [string]$SourceBoard = (Join-Path $PSScriptRoot '..\docs\brand-assets\tinglu-brand-board-v2.png')
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sourcePath = [IO.Path]::GetFullPath($SourceBoard)
$exportRoot = Join-Path $repositoryRoot 'docs\brand-assets\exports'
$sizeRoot = Join-Path $exportRoot 'png'
$webAssetRoot = Join-Path $repositoryRoot 'Examples\WhisperDesktop.Web\src\assets'
$wpfAssetRoot = Join-Path $repositoryRoot 'Examples\WhisperDesktop.Wpf\Assets'

if (-not (Test-Path -LiteralPath $sourcePath)) {
    throw "Brand board not found: $sourcePath"
}

foreach ($directory in @($exportRoot, $sizeRoot, $webAssetRoot, $wpfAssetRoot)) {
    $fullDirectory = [IO.Path]::GetFullPath($directory)
    if (-not $fullDirectory.StartsWith($repositoryRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Output directory escaped the repository: $fullDirectory"
    }
    New-Item -ItemType Directory -Path $fullDirectory -Force | Out-Null
}

Add-Type -AssemblyName System.Drawing

function New-ResizedBitmap {
    param(
        [Parameter(Mandatory)] [System.Drawing.Image]$Source,
        [Parameter(Mandatory)] [int]$Size
    )

    $output = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $output.SetResolution(96, 96)
    $graphics = [System.Drawing.Graphics]::FromImage($output)
    try {
        $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.DrawImage($Source, (New-Object System.Drawing.Rectangle(0, 0, $Size, $Size)))
    }
    finally {
        $graphics.Dispose()
    }
    return $output
}

function Remove-NearWhiteBackground {
    param([Parameter(Mandatory)] [System.Drawing.Bitmap]$Bitmap)

    $rectangle = New-Object System.Drawing.Rectangle(0, 0, $Bitmap.Width, $Bitmap.Height)
    $data = $Bitmap.LockBits(
        $rectangle,
        [System.Drawing.Imaging.ImageLockMode]::ReadWrite,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)

    try {
        $byteCount = [Math]::Abs($data.Stride) * $Bitmap.Height
        $pixels = New-Object byte[] $byteCount
        [Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixels, 0, $byteCount)

        for ($index = 0; $index -lt $pixels.Length; $index += 4) {
            $blue = [int]$pixels[$index]
            $green = [int]$pixels[$index + 1]
            $red = [int]$pixels[$index + 2]
            $maxDifference = [Math]::Max(255 - $red, [Math]::Max(255 - $green, 255 - $blue))

            if ($maxDifference -le 5) {
                $pixels[$index + 3] = 0
                continue
            }

            $alpha = if ($maxDifference -ge 42) { 1.0 } else { ($maxDifference - 5.0) / 37.0 }
            $alphaByte = [Math]::Round($alpha * 255)

            foreach ($offset in 0..2) {
                $channel = [int]$pixels[$index + $offset]
                $foreground = ($channel - 255.0 * (1.0 - $alpha)) / $alpha
                $pixels[$index + $offset] = [byte][Math]::Round([Math]::Max(0.0, [Math]::Min(255.0, $foreground)))
            }
            $pixels[$index + 3] = [byte]$alphaByte
        }

        [Runtime.InteropServices.Marshal]::Copy($pixels, 0, $data.Scan0, $byteCount)
    }
    finally {
        $Bitmap.UnlockBits($data)
    }
}

function Write-MultiResolutionIcon {
    param(
        [Parameter(Mandatory)] [System.Drawing.Image]$Source,
        [Parameter(Mandatory)] [int[]]$Sizes,
        [Parameter(Mandatory)] [string]$Path
    )

    $frames = New-Object System.Collections.Generic.List[byte[]]
    foreach ($size in $Sizes) {
        $resized = New-ResizedBitmap -Source $Source -Size $size
        $stream = New-Object IO.MemoryStream
        try {
            $resized.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            $frames.Add($stream.ToArray())
        }
        finally {
            $stream.Dispose()
            $resized.Dispose()
        }
    }

    $file = [IO.File]::Create($Path)
    $writer = New-Object IO.BinaryWriter($file)
    try {
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$Sizes.Count)

        $offset = 6 + 16 * $Sizes.Count
        for ($index = 0; $index -lt $Sizes.Count; $index++) {
            $size = $Sizes[$index]
            $dimension = if ($size -ge 256) { [byte]0 } else { [byte]$size }
            $writer.Write($dimension)
            $writer.Write($dimension)
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([uint32]$frames[$index].Length)
            $writer.Write([uint32]$offset)
            $offset += $frames[$index].Length
        }

        foreach ($frame in $frames) {
            $writer.Write($frame)
        }
    }
    finally {
        $writer.Dispose()
        $file.Dispose()
    }
}

$masterPng = Join-Path $exportRoot 'tinglu-icon-1024.png'
$exportIco = Join-Path $exportRoot 'Tinglu.ico'
$webPng = Join-Path $webAssetRoot 'tinglu-icon.png'
$wpfIco = Join-Path $wpfAssetRoot 'Tinglu.ico'
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)

$board = New-Object System.Drawing.Bitmap($sourcePath)
try {
    # The generated board is 1774x887. These ratios isolate the left icon while
    # retaining a small safe margin; they remain stable if the board is rescaled.
    $cropX = [Math]::Round($board.Width * 0.081)
    $cropY = [Math]::Round($board.Height * 0.177)
    $cropSize = [Math]::Round($board.Height * 0.645)
    if ($cropX + $cropSize -gt $board.Width -or $cropY + $cropSize -gt $board.Height) {
        throw 'Calculated icon crop falls outside the brand board.'
    }

    $crop = New-Object System.Drawing.Bitmap($cropSize, $cropSize, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($crop)
    try {
        $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $graphics.DrawImage(
            $board,
            (New-Object System.Drawing.Rectangle(0, 0, $cropSize, $cropSize)),
            (New-Object System.Drawing.Rectangle($cropX, $cropY, $cropSize, $cropSize)),
            [System.Drawing.GraphicsUnit]::Pixel)
    }
    finally {
        $graphics.Dispose()
    }

    Remove-NearWhiteBackground -Bitmap $crop
    $master = New-ResizedBitmap -Source $crop -Size 1024
    try {
        $master.Save($masterPng, [System.Drawing.Imaging.ImageFormat]::Png)
        $webIcon = New-ResizedBitmap -Source $master -Size 256
        try {
            $webIcon.Save($webPng, [System.Drawing.Imaging.ImageFormat]::Png)
        }
        finally {
            $webIcon.Dispose()
        }
        Write-MultiResolutionIcon -Source $master -Sizes $sizes -Path $exportIco
        Copy-Item -LiteralPath $exportIco -Destination $wpfIco -Force

        foreach ($size in $sizes) {
            $resized = New-ResizedBitmap -Source $master -Size $size
            try {
                $resized.Save((Join-Path $sizeRoot "tinglu-icon-$size.png"), [System.Drawing.Imaging.ImageFormat]::Png)
            }
            finally {
                $resized.Dispose()
            }
        }
    }
    finally {
        $master.Dispose()
        $crop.Dispose()
    }
}
finally {
    $board.Dispose()
}

Get-Item -LiteralPath $masterPng, $exportIco, $webPng, $wpfIco |
    Select-Object FullName, Length, LastWriteTime
