# Prints the Cities: Skylines II install directory without assuming a store.
# Order: explicit CIAB_GAME_PATH, the game's own Player.log Mono path, then common store locations.
$ErrorActionPreference = 'Stop'
function Test-GamePath([string]$path) { $path -and (Test-Path -LiteralPath (Join-Path $path 'Cities2_Data\Managed\Game.dll')) }
if (Test-GamePath $env:CIAB_GAME_PATH) { return $env:CIAB_GAME_PATH }
$logDir = Join-Path $env:USERPROFILE 'AppData\LocalLow\Colossal Order\Cities Skylines II'
foreach ($log in @('Player.log', 'Player-prev.log')) {
    $file = Join-Path $logDir $log
    if (!(Test-Path -LiteralPath $file)) { continue }
    $line = Select-String -LiteralPath $file -Pattern "Mono path\[0\] = '(.+)/Cities2_Data/Managed'" | Select-Object -First 1
    if ($line) {
        $candidate = $line.Matches[0].Groups[1].Value -replace '/', '\'
        if (Test-GamePath $candidate) { return $candidate }
    }
}
foreach ($candidate in @('C:\Program Files (x86)\Steam\steamapps\common\Cities Skylines II', 'C:\XboxGames\Cities- Skylines II - PC Edition\Content')) {
    if (Test-GamePath $candidate) { return $candidate }
}
throw 'Cities: Skylines II not found. Pass -GamePath or set CIAB_GAME_PATH to the folder containing Cities2_Data.'
