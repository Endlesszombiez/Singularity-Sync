param(
    [ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64',
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot
$localSdk = Join-Path $repoRoot '.tools/dotnet/dotnet.exe'
$dotnetPath = if (Test-Path -LiteralPath $localSdk) { $localSdk } else { (Get-Command dotnet -ErrorAction Stop).Source }
Push-Location $repoRoot
try {
    if (-not $SkipTests) {
        & $dotnetPath run --project SingularitySync.Tests -c Release
        if ($LASTEXITCODE -ne 0) { throw 'Integration checks failed.' }
    }
    $publishDirectory = Join-Path $repoRoot "artifacts/$Runtime"
    & $dotnetPath publish SingularitySync.App -c Release -r $Runtime --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o $publishDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination $publishDirectory
    Copy-Item -LiteralPath (Join-Path $repoRoot 'scripts/Enable-LanFirewall.ps1') -Destination $publishDirectory
    $archive = Join-Path $repoRoot "artifacts/SingularitySync-$Runtime.zip"
    Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $archive -Force
    Write-Host "Portable executable: $publishDirectory/SingularitySync.exe"
    Write-Host "Shareable archive: $archive"
}
finally { Pop-Location }
