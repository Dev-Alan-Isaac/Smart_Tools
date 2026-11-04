<#
.SYNOPSIS
  Builds Smart Tools for release: ONE self-contained folder (and a zip of it) that works both ways.
  The folder has no portable.txt - the first time it is started, the app asks the person whether to Install
  or Run it (portable), and Settings can switch later.
.EXAMPLE
  .\Tools\Publish.ps1
#>
$ErrorActionPreference = 'Stop'

$project = Join-Path $PSScriptRoot '..\Smart Tools.WPF.csproj'
$out     = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\publish\win-x64'))
$zipDir  = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\publish'))

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish $project -p:PublishProfile=win-x64
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

# A portable.txt here would skip the Install / Run question, so make sure none was copied in.
Remove-Item (Join-Path $out 'portable.txt') -ErrorAction SilentlyContinue
Remove-Item (Join-Path $out 'UserData') -Recurse -Force -ErrorAction SilentlyContinue

$version = (Get-Item (Join-Path $out 'SmartTools.exe')).VersionInfo.ProductVersion -replace '\+.*$', ''
if (-not $version) { $version = '0.0.0' }
$zip = Join-Path $zipDir "SmartTools-$version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip
Write-Host "Folder: $out"
Write-Host "Zip:    $zip"
