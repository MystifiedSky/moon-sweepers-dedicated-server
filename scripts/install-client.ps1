param([Parameter(Mandatory = $true)][string]$GameDir)
$ErrorActionPreference = "Stop"
$gameRoot = (Resolve-Path -LiteralPath $GameDir).Path
if (-not (Test-Path -LiteralPath (Join-Path $gameRoot 'MoonSweepers.exe')) -or
    -not (Test-Path -LiteralPath (Join-Path $gameRoot 'BepInEx/core'))) {
    throw 'Select a Moon Sweepers installation with BepInEx and the game patcher installed.'
}
$activeClient = Get-Process MoonSweepers -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq (Join-Path $gameRoot 'MoonSweepers.exe') }
if ($activeClient) { throw 'Close Moon Sweepers before updating its client files.' }
$kitRoot = Split-Path -Parent $PSScriptRoot
$dll = Join-Path $kitRoot 'mod/MoonDedicated.dll'
if (-not (Test-Path -LiteralPath $dll)) { throw 'MoonDedicated.dll is missing from mod/.' }
$compatibility = Join-Path $kitRoot 'mod/MoonLoaderCompatibility.dll'
if (-not (Test-Path -LiteralPath $compatibility)) { throw 'MoonLoaderCompatibility.dll is missing from mod/. Extract the complete client ZIP.' }
$target = Join-Path $gameRoot 'BepInEx/plugins/MoonDedicated'
New-Item -ItemType Directory -Path $target -Force | Out-Null
$payload = @($dll)
$browser = @((Join-Path $kitRoot 'moon-ds.exe'), (Join-Path $kitRoot 'target/release/moon-ds.exe')) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if ($browser) { $payload += $browser } # Optional standalone browser; joining is inside the DLL.
foreach ($source in $payload) {
    $destination = Join-Path $target (Split-Path -Leaf $source)
    if (Test-Path -LiteralPath $destination) {
        Copy-Item -LiteralPath $destination -Destination ($destination + '.backup-' + [Guid]::NewGuid().ToString('N'))
    }
    Copy-Item -LiteralPath $source -Destination $destination -Force
    if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash) {
        throw "Verification failed for $destination"
    }
}
$loaderTarget = Join-Path $gameRoot 'BepInEx/patchers/MoonDedicated'
New-Item -ItemType Directory -Path $loaderTarget -Force | Out-Null
$loaderDestination = Join-Path $loaderTarget 'MoonLoaderCompatibility.dll'
if (Test-Path -LiteralPath $loaderDestination) {
    Copy-Item -LiteralPath $loaderDestination -Destination ($loaderDestination + '.backup-' + [Guid]::NewGuid().ToString('N'))
}
Copy-Item -LiteralPath $compatibility -Destination $loaderDestination -Force
if ((Get-FileHash -LiteralPath $compatibility -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $loaderDestination -Algorithm SHA256).Hash) { throw 'Loader installation verification failed' }
Write-Host "Loader compatibility installed in $loaderTarget."
Write-Host "Client installed in $target. Start Moon Sweepers and choose JOIN SERVER."
