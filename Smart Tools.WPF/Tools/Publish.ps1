<#
.SYNOPSIS
  Builds Smart Tools for release. Two downloads come out of ONE publish:
    1. SmartTools-<ver>-win-x64.zip  - a self-contained folder. It has no portable.txt: the first time the app starts the person
       chooses Install or Run (portable), and Settings > Installation can switch later.
    2. SmartTools-<ver>-win-x64.msi  - the classic Setup wizard (SmartToolsWPF.Installer, WiX) for people who just want Next, Next, Finish.
  Both carry .NET, so neither needs anything installed on the PC.
.PARAMETER SkipMsi
  Only make the zip (the MSI step takes a few minutes: it compresses about 700 MB).
.EXAMPLE
  .\Tools\Publish.ps1
  .\Tools\Publish.ps1 -SkipMsi
#>
param([switch]$SkipMsi)

$ErrorActionPreference = 'Stop'

$project   = Join-Path $PSScriptRoot '..\Smart Tools.WPF.csproj'
$installer = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\SmartToolsWPF.Installer\SmartToolsWPF.Installer.wixproj'))
$out       = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\publish\win-x64'))
$zipDir    = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\publish'))

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish $project -p:PublishProfile=win-x64
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

# A portable.txt here would skip the Install / Run question, so make sure none was copied in.
Remove-Item (Join-Path $out 'portable.txt') -ErrorAction SilentlyContinue
Remove-Item (Join-Path $out 'UserData') -Recurse -Force -ErrorAction SilentlyContinue

# The one version number lives in Smart Tools.WPF.csproj (<Version>); the exe, the zip name and the MSI all read it from there.
$version = (Get-Item (Join-Path $out 'SmartTools.exe')).VersionInfo.ProductVersion -replace '\+.*$', ''
if (-not $version) { $version = '0.0.0' }

$zip = Join-Path $zipDir "SmartTools-$version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip
Write-Host "Folder: $out"
Write-Host "Zip:    $zip"

if ($SkipMsi) { return }

# An MSI version is four numbers at most (a.b.c.d): drop anything after the first dash.
$msiVersion = ($version -split '-')[0]
dotnet build $installer -c Release "-p:ProductVersion=$msiVersion" "-p:PublishDir=$out"
if ($LASTEXITCODE -ne 0) { throw 'Building the installer failed.' }

$built = Get-ChildItem (Join-Path (Split-Path $installer) 'bin') -Recurse -Filter 'SmartTools-Setup.msi' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $built) { throw 'SmartTools-Setup.msi was not produced.' }
$msi = Join-Path $zipDir "SmartTools-$version-win-x64.msi"
Copy-Item $built.FullName $msi -Force
Write-Host "MSI:    $msi"
