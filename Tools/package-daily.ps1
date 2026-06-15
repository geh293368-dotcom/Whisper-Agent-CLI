[CmdletBinding()]
param(
    [ValidatePattern('^\d{4}-\d{2}-\d{2}$')]
    [string]$Date = (Get-Date -Format 'yyyy-MM-dd'),

    [switch]$SkipBuild,
    [switch]$IncludeSymbols
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $repoRoot 'Examples\WhisperDesktop.Wpf\WhisperDesktop.Wpf.csproj'
$existingBuildOutput = Join-Path $repoRoot 'Examples\WhisperDesktop.Wpf\bin\x64\Release\net9.0-windows'
$isolatedBuildOutput = Join-Path $repoRoot ".tmp\daily-build-$Date"
$dailyRoot = Join-Path $repoRoot 'Releases\Daily'
$dateRoot = Join-Path $dailyRoot $Date
$packageName = "WhisperDesktop-$Date-win-x64"
$finalApp = Join-Path $dateRoot 'WhisperDesktop'
$finalZip = Join-Path $dateRoot "$packageName.zip"
$stagingRoot = Join-Path $repoRoot ".tmp\daily-package-$Date"
$stagingApp = Join-Path $stagingRoot 'WhisperDesktop'
$stagingZip = Join-Path $stagingRoot "$packageName.zip"

if (-not $SkipBuild) {
    Write-Host "Building Release version..." -ForegroundColor Cyan
    if (Test-Path $isolatedBuildOutput) {
        Remove-Item -LiteralPath $isolatedBuildOutput -Recurse -Force
    }
    & dotnet build $project -c Release -p:Platform=x64 -o $isolatedBuildOutput
    if ($LASTEXITCODE -ne 0) {
        throw "Release build failed with exit code $LASTEXITCODE."
    }
    $buildOutput = $isolatedBuildOutput
} else {
    $buildOutput = $existingBuildOutput
}

if (-not (Test-Path (Join-Path $buildOutput 'WhisperDesktop.Modern.exe'))) {
    throw "Build output was not found at '$buildOutput'. Run without -SkipBuild first."
}

if (Test-Path $stagingRoot) {
    Remove-Item -LiteralPath $stagingRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $stagingApp -Force | Out-Null

Write-Host "Preparing daily package $Date..." -ForegroundColor Cyan
Get-ChildItem -LiteralPath $buildOutput -Force | Where-Object {
    $_.Name -ne 'WhisperDesktop.Modern.exe.WebView2' -and
    ($IncludeSymbols -or $_.Extension -notin @('.pdb', '.xml'))
} | Copy-Item -Destination $stagingApp -Recurse -Force

$commit = (& git -C $repoRoot rev-parse --short HEAD 2>$null)
if ($LASTEXITCODE -ne 0) { $commit = 'unknown' }
$branch = (& git -C $repoRoot branch --show-current 2>$null)
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($branch)) { $branch = 'unknown' }
$dirty = -not [string]::IsNullOrWhiteSpace((& git -C $repoRoot status --porcelain 2>$null | Out-String))

@"
WhisperDesktop daily build
Package date: $Date
Built at: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')
Git branch: $branch
Git commit: $commit
Uncommitted changes: $dirty
Configuration: Release / x64
Runtime: framework-dependent (.NET 9 Desktop Runtime required)
"@ | Set-Content -LiteralPath (Join-Path $stagingApp 'BUILD-INFO.txt') -Encoding UTF8

Compress-Archive -Path $stagingApp -DestinationPath $stagingZip -CompressionLevel Optimal -Force

# Replace the dated package only after the new package has been fully prepared.
New-Item -ItemType Directory -Path $dailyRoot -Force | Out-Null
if (Test-Path $dateRoot) {
    Remove-Item -LiteralPath $dateRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $dateRoot -Force | Out-Null
Move-Item -LiteralPath $stagingApp -Destination $finalApp
Move-Item -LiteralPath $stagingZip -Destination $finalZip
Remove-Item -LiteralPath $stagingRoot -Recurse -Force
if (-not $SkipBuild -and (Test-Path $isolatedBuildOutput)) {
    Remove-Item -LiteralPath $isolatedBuildOutput -Recurse -Force
}

@"
Latest daily package: $Date
Folder: Releases\Daily\$Date\WhisperDesktop
Archive: Releases\Daily\$Date\$packageName.zip
"@ | Set-Content -LiteralPath (Join-Path $dailyRoot 'LATEST.txt') -Encoding UTF8

Write-Host "Daily package created successfully:" -ForegroundColor Green
Write-Host "  $finalApp"
Write-Host "  $finalZip"
