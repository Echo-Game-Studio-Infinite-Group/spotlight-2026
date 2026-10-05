param(
    [string]$UnityExe = "D:\Unity Hub\Editor\2022.3.33f1\Editor\Unity.exe",
    [string]$UnityProject = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrEmpty($UnityProject)) {
    $UnityProject = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
}

$referenceDirectory = Join-Path $UnityProject "Logs\AudioDspReference"
$nativeDirectory = Join-Path $UnityProject "Logs\AudioDspNative"
$unityLog = Join-Path $UnityProject "Logs\audio-dsp-reference-export.log"
$testExecutable = Join-Path $UnityProject "spotlight-2026_WwiseProject\Plugins\SpotlightActionSource\Build\bin\Release\tests\SpotlightActionDspTests.exe"

if (-not (Test-Path -LiteralPath $UnityExe)) {
    throw "Unity Editor not found: $UnityExe"
}
if (-not (Test-Path -LiteralPath $testExecutable)) {
    throw "DSP test executable not found. Run build_spotlight_action_source.ps1 first."
}

$arguments = @(
    "-batchmode",
    "-nographics",
    "-projectPath", $UnityProject,
    "-executeMethod", "SpotlightActionDspReferenceExporter.ExportReference",
    "-quit",
    "-logFile", $unityLog
)

$process = Start-Process `
    -FilePath $UnityExe `
    -ArgumentList $arguments `
    -Wait `
    -PassThru `
    -NoNewWindow

if ($process.ExitCode -ne 0) {
    throw "Unity reference export failed. See $unityLog"
}

& $testExecutable $nativeDirectory $referenceDirectory
if ($LASTEXITCODE -ne 0) {
    throw "DSP parity test failed."
}
