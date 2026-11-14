Add-Type -AssemblyName PresentationCore,PresentationFramework,WindowsBase
$brandResources = [Windows.Markup.XamlReader]::Parse((Get-Content 'src/VideoWallRW.App/Assets/Brand.xaml' -Raw))
$brandDrawing = $brandResources['BrandMark'].Drawing
$frames = @()
foreach ($edge in @(16,24,32,48,64,128,256)) {
 $visual = [Windows.Media.DrawingVisual]::new()
 $dc = $visual.RenderOpen()
 $dc.PushTransform([Windows.Media.ScaleTransform]::new($edge/64.0,$edge/64.0))
 $dc.DrawDrawing($brandDrawing)
 $dc.Pop()
 $dc.Close()
 $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($edge,$edge,96,96,[Windows.Media.PixelFormats]::Pbgra32)
 $bitmap.Render($visual)
 $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
 $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
 $stream = [IO.MemoryStream]::new()
 $encoder.Save($stream)
 $frames += ,@($edge,$stream.ToArray())
 $stream.Dispose()
 if ($edge -eq 256) { [IO.File]::WriteAllBytes((Join-Path (Get-Location) 'src/VideoWallRW.App/Assets/AppIcon.png'),$frames[-1][1]) }
}
$iconPath = Join-Path (Get-Location) 'src/VideoWallRW.App/Assets/AppIcon.ico'
$icon = [IO.BinaryWriter]::new([IO.File]::Create($iconPath))
$icon.Write([uint16]0); $icon.Write([uint16]1); $icon.Write([uint16]$frames.Count)
$offset = 6 + 16*$frames.Count
foreach ($frame in $frames) {
 $dimension = if ($frame[0] -eq 256) { 0 } else { $frame[0] }
 $icon.Write([byte]$dimension); $icon.Write([byte]$dimension); $icon.Write([byte]0); $icon.Write([byte]0)
 $icon.Write([uint16]1); $icon.Write([uint16]32); $icon.Write([uint32]$frame[1].Length); $icon.Write([uint32]$offset)
 $offset += $frame[1].Length
}
foreach ($frame in $frames) { $icon.Write([byte[]]$frame[1]) }
$icon.Dispose()
Write-Output 'Marca vetorial renderizada em sete tamanhos de ícone.'
