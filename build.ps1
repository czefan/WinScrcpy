param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$Publish,
    [switch]$SelfContained,
    [switch]$RegenerateIcon
)

$ErrorActionPreference = 'Stop'

$projectPath = Join-Path $PSScriptRoot 'WinScrcpy.csproj'
$iconPath = Join-Path $PSScriptRoot 'assets\app.ico'
$pngPath = Join-Path $PSScriptRoot 'assets\icon.png'
$iconScript = Join-Path $PSScriptRoot 'scripts\generate-icon.ps1'

if ($RegenerateIcon -or -not (Test-Path -LiteralPath $iconPath) -or -not (Test-Path -LiteralPath $pngPath)) {
    & $iconScript -Force:$RegenerateIcon
}

$outputExe = if ($Publish) {
    Join-Path $PSScriptRoot "bin\$Configuration\net10.0-windows\win-x64\publish\WinScrcpy.exe"
} else {
    Join-Path $PSScriptRoot "bin\$Configuration\net10.0-windows\WinScrcpy.exe"
}

Get-Process -Name 'WinScrcpy' -ErrorAction SilentlyContinue | Where-Object {
    $_.Path -and ($_.Path -ieq $outputExe)
} | ForEach-Object {
    Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
    $_.WaitForExit(3000) | Out-Null
}

if ($Publish) {
    $scArg = if ($SelfContained) { 'true' } else { 'false' }
    & dotnet publish $projectPath -c $Configuration -r win-x64 --self-contained $scArg -p:PublishSingleFile=true
} else {
    & dotnet build $projectPath -c $Configuration
}

if ($LASTEXITCODE -ne 0) {
    throw 'dotnet build/publish failed for WinScrcpy.'
}

Write-Output "Built: $outputExe"
