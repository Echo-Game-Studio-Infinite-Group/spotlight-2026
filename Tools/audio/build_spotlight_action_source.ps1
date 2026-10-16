param(
    [string]$WwiseRoot = "D:\Wwise_2024.1.17.9170",
    [string]$UnityProject = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrEmpty($UnityProject)) {
    $UnityProject = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
}

$pluginRoot = Join-Path $UnityProject "spotlight-2026_WwiseProject\Plugins\SpotlightActionSource"
$buildRoot = Join-Path $pluginRoot "Build"
$cmake = Join-Path $WwiseRoot "Tools\cmake\windows-x86_64\bin\cmake.exe"
$sdk = Join-Path $WwiseRoot "SDK"
$buildToolsRoot = "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools"

if (-not (Test-Path -LiteralPath $cmake)) {
    throw "CMake not found: $cmake"
}
if (-not (Test-Path -LiteralPath $sdk)) {
    throw "Wwise SDK not found: $sdk"
}
if (-not (Test-Path -LiteralPath $buildToolsRoot)) {
    throw "Visual Studio 2022 Build Tools not found: $buildToolsRoot"
}

& $cmake `
    -S $pluginRoot `
    -B $buildRoot `
    -G "Visual Studio 17 2022" `
    -A x64 `
    -T v143 `
    "-DCMAKE_GENERATOR_INSTANCE=$buildToolsRoot" `
    "-DWWISE_SDK=$sdk"

if ($LASTEXITCODE -ne 0) {
    throw "CMake configure failed."
}

& $cmake --build $buildRoot --config Release --parallel
if ($LASTEXITCODE -ne 0) {
    throw "CMake build failed."
}

$authoringSource = Join-Path $buildRoot "bin\Release\authoring"
$runtimeSource = Join-Path $buildRoot "bin\Release\runtime"
$authoringDestination = Join-Path $WwiseRoot "Authoring\x64\Release\bin\Plugins"
$runtimeDestination = Join-Path $UnityProject "Assets\Wwise\API\Runtime\Plugins\Windows\x86_64\DSP"

New-Item -ItemType Directory -Path $authoringDestination -Force | Out-Null
New-Item -ItemType Directory -Path $runtimeDestination -Force | Out-Null

Copy-Item -LiteralPath (Join-Path $authoringSource "SpotlightActionSource.dll") -Destination $authoringDestination -Force
Copy-Item -LiteralPath (Join-Path $authoringSource "SpotlightActionSource.xml") -Destination $authoringDestination -Force
Copy-Item -LiteralPath (Join-Path $runtimeSource "SpotlightActionSource.dll") -Destination $runtimeDestination -Force

Write-Host "SpotlightActionSource built and installed."
Write-Host "Authoring: $authoringDestination"
Write-Host "Runtime:   $runtimeDestination"
