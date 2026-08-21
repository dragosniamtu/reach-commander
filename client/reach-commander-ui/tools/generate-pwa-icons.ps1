$ErrorActionPreference = 'Stop'
$magick = Get-Command magick -ErrorAction SilentlyContinue
if ($null -eq $magick) {
  throw 'ImageMagick (magick) is required to regenerate the PWA icons.'
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$source = Join-Path $projectRoot 'public/icons/reachcommander-mark.svg'
$outputs = @(
  @{ Name = 'icon-192.png'; Size = 192 },
  @{ Name = 'icon-512.png'; Size = 512 },
  @{ Name = 'icon-maskable-192.png'; Size = 192 },
  @{ Name = 'icon-maskable-512.png'; Size = 512 },
  @{ Name = 'apple-touch-icon.png'; Size = 180 },
  @{ Name = 'favicon-32.png'; Size = 32 }
)

foreach ($output in $outputs) {
  $target = Join-Path $projectRoot "public/icons/$($output.Name)"
  & $magick.Source -background none $source -resize "$($output.Size)x$($output.Size)" -strip $target
  if ($LASTEXITCODE -ne 0) {
    throw "ImageMagick failed while creating $($output.Name)."
  }
}
