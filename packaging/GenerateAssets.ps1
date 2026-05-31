#requires -Version 5.1
<#
.SYNOPSIS
  Generate placeholder MSIX assets (solid-color PNGs) at the required sizes.
.DESCRIPTION
  Real assets should replace these before public release. The placeholders use
  a simple flat color so MakeAppx is happy and the resulting package can be
  inspected end-to-end in CI without committing binary PNGs to the repo.
#>
param(
  [Parameter(Mandatory = $true)] [string] $OutputDir,
  [string] $HexColor = '#2C7BE5'   # accent blue
)

Add-Type -AssemblyName System.Drawing

if (-not (Test-Path $OutputDir)) {
  New-Item -ItemType Directory -Path $OutputDir | Out-Null
}

$color = [System.Drawing.ColorTranslator]::FromHtml($HexColor)

$assets = @(
  @{ Name = 'Square44x44Logo.png';   Width =  44; Height =  44 },
  @{ Name = 'Square150x150Logo.png'; Width = 150; Height = 150 },
  @{ Name = 'Wide310x150Logo.png';   Width = 310; Height = 150 },
  @{ Name = 'StoreLogo.png';         Width =  50; Height =  50 }
)

foreach ($a in $assets) {
  $bmp = New-Object System.Drawing.Bitmap($a.Width, $a.Height)
  $gfx = [System.Drawing.Graphics]::FromImage($bmp)
  $gfx.Clear($color)
  $path = Join-Path $OutputDir $a.Name
  $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
  $gfx.Dispose()
  $bmp.Dispose()
  Write-Host "Wrote $path ($($a.Width)x$($a.Height))"
}
