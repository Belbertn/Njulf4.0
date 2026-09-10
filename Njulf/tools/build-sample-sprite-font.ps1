param(
    [Parameter(Mandatory=$true)][string]$FontPath,
    [string]$OutputDirectory = "$PSScriptRoot/../Njulf.ApiExamples/Assets/Content/Sprites"
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$collection = [Drawing.Text.PrivateFontCollection]::new()
$collection.AddFontFile((Resolve-Path -LiteralPath $FontPath).Path)
$font = [Drawing.Font]::new($collection.Families[0], 24, [Drawing.FontStyle]::Regular, [Drawing.GraphicsUnit]::Pixel)
$bitmap = [Drawing.Bitmap]::new(512, 256, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
$graphics = [Drawing.Graphics]::FromImage($bitmap)
$format = [Drawing.StringFormat]::GenericTypographic.Clone()
$format.FormatFlags = $format.FormatFlags -bor [Drawing.StringFormatFlags]::MeasureTrailingSpaces
try {
    $graphics.Clear([Drawing.Color]::Transparent)
    $graphics.TextRenderingHint = [Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $glyphs = for ($code = 32; $code -le 126; $code++) {
        $index = $code - 32
        $x = ($index % 16) * 32
        $y = [int][Math]::Floor($index / 16) * 40
        $text = [string][char]$code
        $graphics.DrawString($text, $font, [Drawing.Brushes]::White, [Drawing.PointF]::new($x + 1, $y + 1), $format)
        $advance = $graphics.MeasureString($text, $font, [int]100, $format).Width
        @{ codePoint=$code; source=@{ x=$x; y=$y; width=32; height=40 }; offset=@{ x=0; y=0 }; advance=[Math]::Round($advance, 3) }
    }
    $bitmap.Save([IO.Path]::GetFullPath("$OutputDirectory/sample.png"), [Drawing.Imaging.ImageFormat]::Png)
    @{ schemaVersion=1; atlas='sample.png'; lineHeight=32; fallbackCodePoint=63; glyphs=@($glyphs); kerning=@() } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath "$OutputDirectory/sample.njfont.json"
}
finally { $format.Dispose(); $graphics.Dispose(); $bitmap.Dispose(); $font.Dispose(); $collection.Dispose() }
