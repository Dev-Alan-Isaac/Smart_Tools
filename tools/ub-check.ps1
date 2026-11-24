# The three ub-mode checks, runnable from anywhere:
#   .\tools\ub-check.ps1 dryer   [folder]   copied method bodies (default: Smart Tools.WPF)
#   .\tools\ub-check.ps1 crapper [limit]    CRAP score per method of the tested helpers (default limit 10)
#   .\tools\ub-check.ps1 mutator            Stryker mutation test of Smart Tools.Shared
#   .\tools\ub-check.ps1 all
# Needs: dotnet SDK, python 3 on PATH; "dotnet tool restore" fetches Stryker the first time.
param(
    [Parameter(Position = 0)][ValidateSet('dryer', 'crapper', 'mutator', 'all')][string]$Check = 'all',
    [Parameter(Position = 1)][string]$Arg
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
$tests = Join-Path $root 'Smart Tools.WPF.Tests'
$out = Join-Path $root 'artifacts'

function Invoke-Dryer {
    $folder = if ($Arg) { $Arg } else { Join-Path $root 'Smart Tools.WPF' }
    python -I (Join-Path $PSScriptRoot 'dryer.py') $folder 5 40
}

function Invoke-Crapper {
    $limit = if ($Arg) { $Arg } else { '10' }
    $cov = Join-Path $out 'coverage'
    if (Test-Path $cov) { Remove-Item $cov -Recurse -Force }
    dotnet test $tests --nologo --collect:'XPlat Code Coverage' --results-directory $cov
    if ($LASTEXITCODE -ne 0) { throw 'tests failed' }
    $file = Get-ChildItem $cov -Recurse -Filter coverage.cobertura.xml | Select-Object -First 1
    python -I (Join-Path $PSScriptRoot 'crapper.py') $file.FullName $limit
}

function Invoke-Mutator {
    dotnet tool restore | Out-Null
    Push-Location $tests
    try { dotnet tool run dotnet-stryker --output (Join-Path $out 'mutation') } finally { Pop-Location }
}

switch ($Check) {
    'dryer'   { Invoke-Dryer }
    'crapper' { Invoke-Crapper }
    'mutator' { Invoke-Mutator }
    'all'     { Invoke-Dryer; Invoke-Crapper; Invoke-Mutator }
}
