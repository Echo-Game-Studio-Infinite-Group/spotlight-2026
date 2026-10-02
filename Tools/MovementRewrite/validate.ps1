param([ValidateSet('Setup', 'Tests')][string]$Stage = 'Tests')
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$validationRoot = Join-Path $repoRoot 'Temp/MovementRewriteValidation'
$editorPath = 'C:/Program Files/Unity/Hub/Editor/2022.3.33f1/Editor/Unity.exe'
if (-not (Test-Path -LiteralPath $editorPath)) { $editorPath = 'D:/Program Files/Unity/Hub/Editor/2022.3.33f1/Editor/Unity.exe' }
New-Item -ItemType Directory -Path $validationRoot -Force | Out-Null
foreach ($folder in @('Assets', 'Packages', 'ProjectSettings')) {
    & robocopy (Join-Path $repoRoot $folder) (Join-Path $validationRoot $folder) /E /NJH /NJS /NDL /NFL /NP /R:1 /W:1 | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "复制失败：$folder" }
}
$cache = Join-Path $repoRoot 'Library/PackageCache'
& robocopy $cache (Join-Path $validationRoot 'Library/PackageCache') /E /NJH /NJS /NDL /NFL /NP /R:1 /W:1 | Out-Null
if ($LASTEXITCODE -ge 8) { throw '复制 PackageCache 失败' }
$arguments = @('-batchmode', '-nographics', '-projectPath', ('"' + $validationRoot + '"'), '-logFile', ('"' + (Join-Path $validationRoot "$Stage.log") + '"'))
if ($Stage -eq 'Setup') { $arguments += @('-executeMethod', 'MovementSceneSetup.Rebuild', '-quit') }
else { $arguments += @('-runTests', '-testPlatform', 'PlayMode', '-testResults', ('"' + (Join-Path $validationRoot 'PlayMode.xml') + '"')) }
$process = Start-Process -FilePath $editorPath -ArgumentList $arguments -WindowStyle Hidden -PassThru
Write-Output "Unity $Stage PID=$($process.Id)；日志：$validationRoot/$Stage.log"
$process.WaitForExit()
Write-Output "Unity exit=$($process.ExitCode)"
if ($process.ExitCode -ne 0) { throw "Unity $Stage 失败，请检查日志" }
