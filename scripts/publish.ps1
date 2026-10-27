param([switch]$SmokeTest)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location $projectRoot
try {
    dotnet build CricketAuction.App/CricketAuction.App.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
    dotnet publish CricketAuction.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o artifacts/win-x64
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    Copy-Item -LiteralPath README.md -Destination artifacts/win-x64/README.md -Force
    Copy-Item -LiteralPath samples/players.csv -Destination artifacts/win-x64/players.csv -Force
    if ($SmokeTest) {
    $smokeRoot = Join-Path $projectRoot ("artifacts/acceptance/release-smoke-{0}" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
    New-Item -ItemType Directory -Path $smokeRoot -Force | Out-Null
    $smoke = Start-Process -FilePath (Join-Path $projectRoot 'artifacts/win-x64/CricketAuction.App.exe') -ArgumentList @('--smoke-test', '--data-dir', ('"' + $smokeRoot + '"')) -WindowStyle Hidden -PassThru -Wait
    if ($smoke.ExitCode -ne 0) { throw "Published smoke test failed with exit code $($smoke.ExitCode). See $smokeRoot/smoke-error.txt" }
    if (-not (Test-Path (Join-Path $smokeRoot 'smoke-ok.txt'))) { throw "Published smoke test did not write smoke-ok.txt. See $smokeRoot" }
    }
    Write-Host 'Ready: artifacts/win-x64/CricketAuction.App.exe. Distribute the entire win-x64 folder.'
    if ($SmokeTest) { Write-Host "Smoke verification: $smokeRoot/smoke-ok.txt" }
} finally { Pop-Location }
