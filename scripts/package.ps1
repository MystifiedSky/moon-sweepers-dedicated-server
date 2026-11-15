# Builds the release zip for this machine: dist/moon-ds-<os>-x86_64.zip
#   moon-ds(.exe)  +  mod/MoonDedicated.dll  +  docs/
$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")
dotnet restore mod/MoonLoaderCompatibility --locked-mode
if ($LASTEXITCODE -ne 0) { throw "Loader restore failed" }
dotnet build -c Release mod/MoonLoaderCompatibility --no-restore
if ($LASTEXITCODE -ne 0) { throw "Loader build failed" }
dotnet restore mod/MoonDedicated --locked-mode
if ($LASTEXITCODE -ne 0) { throw "Mod restore failed" }
dotnet build -c Release mod/MoonDedicated --no-restore
if ($LASTEXITCODE -ne 0) { throw "Mod build failed" }
cargo build --release --locked
if ($LASTEXITCODE -ne 0) { throw "Manager build failed" }
$releaseVersion = (Select-String -LiteralPath Cargo.toml -Pattern '^version = "([0-9.]+)"').Matches[0].Groups[1].Value
python scripts/package-release.py --platform windows --version $releaseVersion
if ($LASTEXITCODE -ne 0) { throw "Packaging or verification failed" }
python scripts/verify-client-package.py --version $releaseVersion
if ($LASTEXITCODE -ne 0) { throw "Client archive validation failed" }
Write-Host "dist/moon-client.zip and dist/moon-ds-windows-x86_64.zip"
