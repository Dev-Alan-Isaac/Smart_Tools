<#
.SYNOPSIS
  Builds the portable version of Smart Tools: a self-contained folder with a portable.txt
  marker (settings and logs go to .\UserData beside the exe) and a zip of it.
.EXAMPLE
  .\tools\Publish-Portable.ps1
#>
$ErrorActionPreference = 'Stop'

$project = Join-Path $PSScriptRoot '..\Smart Tools.WPF.csproj'
$out     = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\publish\portable'))
$zipDir  = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\publish'))

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish $project -p:PublishProfile=Portable
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

# The marker that switches the app to portable mode (see Modules\AppPaths.cs).
New-Item -ItemType File -Path (Join-Path $out 'portable.txt') -Force | Out-Null

$version = (Get-Item (Join-Path $out 'SmartTools.exe')).VersionInfo.ProductVersion -replace '\+.*$', ''
if (-not $version) { $version = '0.0.0' }
$zip = Join-Path $zipDir "SmartTools-$version-portable-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip
Write-Host "Portable folder: $out"
Write-Host "Zip:             $zip"
