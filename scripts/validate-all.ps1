$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Write-Section {
    param([Parameter(Mandatory = $true)][string]$Title)

    Write-Host ""
    Write-Host "==== $Title ===="
}

function Invoke-Native {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [string[]]$Arguments = @()
    )

    & $FilePath @Arguments
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "Command failed with exit code ${exitCode}: $FilePath $($Arguments -join ' ')"
    }
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$enteredRepo = $false
$enteredLean = $false

try {
    Push-Location $repoRoot
    $enteredRepo = $true

    Write-Section "C# test suite"
    Invoke-Native -FilePath "dotnet" -Arguments @("test", ".\KatLang.slnx", "-p:UseSharedCompilation=false")

    Write-Section "Git diff check"
    Invoke-Native -FilePath "git" -Arguments @("diff", "--check")

    Write-Section "Lean CoreTests"
    Push-Location ".\lean"
    $enteredLean = $true
    Invoke-Native -FilePath "lake" -Arguments @("build", "CoreTests")

    Write-Section "Lean AstDemo"
    Invoke-Native -FilePath "lake" -Arguments @("build", "AstDemo")

    Pop-Location
    $enteredLean = $false

    Write-Section "Validation complete"
}
catch {
    Write-Host ""
    Write-Host "Validation failed: $($_.Exception.Message)"
    exit 1
}
finally {
    if ($enteredLean) {
        Pop-Location
    }

    if ($enteredRepo) {
        Pop-Location
    }
}