param(
    [ValidateSet('Compile', 'Examples', 'TextInstall', 'EditMode', 'PlayMode', 'MapBaseline')][string]$Stage = 'Compile',
    [string]$ValidationDirectory = 'Tools/map-flow/obj/Validation'
)
$ErrorActionPreference = 'Stop'
$flowRepo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
if ($Stage -eq 'Compile') {
    & (Join-Path $flowRepo 'Tools/ActionSequences/validate.ps1') -Stage Compile
    exit $LASTEXITCODE
}
$flowValidation = [IO.Path]::GetFullPath((Join-Path $flowRepo $ValidationDirectory))
if (-not $flowValidation.StartsWith($flowRepo + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Validation directory must stay inside the repository.' }
New-Item -ItemType Directory -Path $flowValidation -Force | Out-Null
foreach ($flowFolder in @('Assets', 'Packages', 'ProjectSettings', 'Library/PackageCache')) {
    & robocopy (Join-Path $flowRepo $flowFolder) (Join-Path $flowValidation $flowFolder) /E /NJH /NJS /NDL /NFL /NP /R:1 /W:1 | Out-Null
    if ($LASTEXITCODE -ge 8) { throw ('Copy failed: ' + $flowFolder) }
}
$flowEditor = 'C:/Program Files/Unity/Hub/Editor/2022.3.33f1/Editor/Unity.exe'
if (-not (Test-Path -LiteralPath $flowEditor)) { $flowEditor = 'D:/Program Files/Unity/Hub/Editor/2022.3.33f1/Editor/Unity.exe' }
$flowLog = Join-Path $flowValidation ($Stage + '.log')
$flowStarted = [DateTime]::UtcNow
$flowArgs = @('-batchmode', '-nographics', '-projectPath', ('"' + $flowValidation + '"'), '-logFile', ('"' + $flowLog + '"'))
if ($Stage -eq 'Examples') { $flowArgs += @('-executeMethod', 'GameJam.FlowEditor.FlowExampleSetup.Build', '-quit') }
elseif ($Stage -eq 'TextInstall') { $flowArgs += @('-executeMethod', 'GameJam.FlowEditor.FlowTextAuthoring.Build', '-quit') }
else {
    $flowMode = if ($Stage -eq 'MapBaseline') { 'PlayMode' } else { $Stage }
    $flowFilter = if ($Stage -eq 'MapBaseline') { 'MapStreamingTests;TeamWhiteboxTests' } else { 'GameJam.FlowTests' }
    $flowArgs += @('-runTests', '-testPlatform', $flowMode, '-testFilter', $flowFilter, '-testResults', ('"' + (Join-Path $flowValidation ($Stage + '.xml')) + '"'))
}
$flowProcess = Start-Process -FilePath $flowEditor -ArgumentList $flowArgs -WindowStyle Hidden -PassThru
Write-Output "Unity $Stage PID=$($flowProcess.Id); log=$flowLog"
$flowProcess.WaitForExit()
if ($flowProcess.ExitCode -ne 0) { throw "Unity $Stage failed, exit=$($flowProcess.ExitCode). See $flowLog" }
if ($Stage -notin @('Examples', 'TextInstall')) {
    $flowResultFile = Join-Path $flowValidation ($Stage + '.xml')
    if (-not (Test-Path -LiteralPath $flowResultFile) -or (Get-Item -LiteralPath $flowResultFile).LastWriteTimeUtc -lt $flowStarted) { throw 'Unity did not produce a fresh test result.' }
    [xml]$flowResult = Get-Content -LiteralPath $flowResultFile -Raw
    Write-Output "Passed=$($flowResult.'test-run'.passed) Failed=$($flowResult.'test-run'.failed)"
    if ($flowResult.'test-run'.result -ne 'Passed') { throw 'Tests failed.' }
}
exit 0
