param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$Publish,
    [switch]$SelfContained,
    [switch]$Compress,
    [switch]$Package,
    [switch]$RegenerateIcon,
    [switch]$Test
)

$ErrorActionPreference = 'Stop'

$projectPath = Join-Path $PSScriptRoot 'WinScrcpy.csproj'
$testProjectPath = Join-Path $PSScriptRoot 'tests\WinScrcpy.Tests\WinScrcpy.Tests.csproj'
$iconPath = Join-Path $PSScriptRoot 'assets\app.ico'
$pngPath = Join-Path $PSScriptRoot 'assets\icon.png'
$iconScript = Join-Path $PSScriptRoot 'scripts\generate-icon.ps1'

if ($Package) {
    $Publish = $true
}

if ($RegenerateIcon -or -not (Test-Path -LiteralPath $iconPath) -or -not (Test-Path -LiteralPath $pngPath)) {
    & $iconScript -Force:$RegenerateIcon
}

$publishFlavor = if ($SelfContained) {
    if ($Compress) { 'selfcontained-compressed' } else { 'selfcontained' }
} else {
    'framework-dependent'
}
$publishDir = Join-Path $PSScriptRoot "bin\$Configuration\net10.0-windows\win-x64\publish-$publishFlavor"
$outputExe = if ($Publish) {
    Join-Path $publishDir 'WinScrcpy.exe'
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
    if (Test-Path -LiteralPath $publishDir) {
        Get-ChildItem -LiteralPath $publishDir -Filter '*.pdb' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
    }
    $scArg = if ($SelfContained) { 'true' } else { 'false' }
    $compressArg = if ($SelfContained -and $Compress) { 'true' } else { 'false' }
    & dotnet publish $projectPath -c $Configuration -r win-x64 --self-contained $scArg -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=$compressArg -p:DebugType=None -p:DebugSymbols=false -o $publishDir
} else {
    & dotnet build $projectPath -c $Configuration
}

if ($LASTEXITCODE -ne 0) {
    throw 'dotnet build/publish failed for WinScrcpy.'
}

Write-Output "Built: $outputExe"

if ($Package) {
    $flavor = "win-x64-$publishFlavor"
    $zipPath = Join-Path $PSScriptRoot "bin\$Configuration\WinScrcpy-$flavor.zip"
    $stagingDir = Join-Path $PSScriptRoot "obj\$Configuration\package-$flavor"
    if (Test-Path -LiteralPath $stagingDir) {
        Remove-Item -LiteralPath $stagingDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null
    Copy-Item -LiteralPath $outputExe -Destination (Join-Path $stagingDir 'WinScrcpy.exe') -Force
    foreach ($extra in @('winscrcpy.example.json', 'LICENSE', 'README.md')) {
        $srcExtra = Join-Path $PSScriptRoot $extra
        if (Test-Path -LiteralPath $srcExtra) {
            Copy-Item -LiteralPath $srcExtra -Destination (Join-Path $stagingDir $extra) -Force
        }
    }
    if (Test-Path -LiteralPath $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }
    Compress-Archive -Path (Join-Path $stagingDir '*') -DestinationPath $zipPath -CompressionLevel Optimal
    Remove-Item -LiteralPath $stagingDir -Recurse -Force
    Write-Output "Packaged: $zipPath"
}

if ($Test) {
    & dotnet test $testProjectPath -c $Configuration
    if ($LASTEXITCODE -ne 0) {
        throw 'dotnet test failed for WinScrcpy.Tests.'
    }
}
