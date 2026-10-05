param(
    [ValidateSet('Compile', 'EditMode', 'PlayMode', 'BaselinePlayMode', 'Examples', 'Install', 'All')][string]$Stage = 'Compile',
    [string]$TestFilter = '',
    [switch]$UseRepositoryPackages
)
$ErrorActionPreference = 'Stop'
if ($Stage -eq 'All') {
    foreach ($taskCheckStage in @('Compile', 'EditMode', 'PlayMode')) {
        & $PSCommandPath -Stage $taskCheckStage -TestFilter $TestFilter -UseRepositoryPackages:$UseRepositoryPackages
        if ($LASTEXITCODE -ne 0) { throw "验证失败：$taskCheckStage" }
    }
    exit 0
}
if ($Stage -eq 'BaselinePlayMode') { throw '集成版本已依赖 Actions；请使用 -Stage PlayMode，不再排除动作源码运行基线。' }
$taskRepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$taskEditorPath = 'C:/Program Files/Unity/Hub/Editor/2022.3.33f1/Editor/Unity.exe'
if (-not (Test-Path -LiteralPath $taskEditorPath)) { $taskEditorPath = 'D:/Program Files/Unity/Hub/Editor/2022.3.33f1/Editor/Unity.exe' }
if (-not (Test-Path -LiteralPath $taskEditorPath)) { throw '未找到冻结版本 Unity 2022.3.33f1' }
$taskEditorData = Join-Path (Split-Path $taskEditorPath) 'Data'

if ($Stage -eq 'Compile') {
    $taskOutput = Join-Path $taskRepoRoot 'Temp/ActionSequenceCompile'
    New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null
    $taskSdkVersion = (& dotnet --version).Trim()
    $taskCsc = Join-Path 'C:/Program Files/dotnet/sdk' "$taskSdkVersion/Roslyn/bincore/csc.dll"
    $taskReferenceRoot = Join-Path $taskEditorData 'NetStandard/ref/2.1.0'
    $taskReferences = @(Get-ChildItem -LiteralPath $taskReferenceRoot -Filter '*.dll')
    $taskReferences += @(Get-ChildItem -LiteralPath (Join-Path $taskEditorData 'Managed/UnityEngine') -Filter '*.dll')
    $taskReferences += @(Get-ChildItem -LiteralPath (Join-Path $taskRepoRoot 'Library/ScriptAssemblies') -Filter '*.dll' | Where-Object { $_.Name -notlike 'GameJam.*' -and $_.Name -ne 'Assembly-CSharp-Editor.dll' })
    $taskCommon = @('-nologo', '-nostdlib+', '-target:library', '-langversion:9', '-define:UNITY_EDITOR')
    $taskCommon += @($taskReferences | ForEach-Object { '-r:"' + $_.FullName + '"' })
    $taskRuntimeDll = Join-Path $taskOutput 'Actions.Runtime.dll'
    $taskRuntimeArgs = $taskCommon + @('-out:"' + $taskRuntimeDll + '"')
    $taskRuntimeArgs += @(Get-ChildItem -LiteralPath (Join-Path $taskRepoRoot 'Assets/Scripts') -Filter '*.cs' -Recurse | ForEach-Object { '"' + $_.FullName + '"' })
    $taskRuntimeRsp = Join-Path $taskOutput 'runtime.rsp'
    Set-Content -LiteralPath $taskRuntimeRsp -Value $taskRuntimeArgs -Encoding utf8
    & dotnet $taskCsc '-noconfig' ('@' + $taskRuntimeRsp)
    if ($LASTEXITCODE -ne 0) { throw '动作运行时编译失败' }
    $taskEditorArgs = $taskCommon + @('-r:"' + $taskRuntimeDll + '"', '-out:"' + (Join-Path $taskOutput 'Actions.Editor.dll') + '"')
    $taskEditorArgs += @(Get-ChildItem -LiteralPath (Join-Path $taskRepoRoot 'Assets/Editor') -Filter '*.cs' -Recurse | ForEach-Object { '"' + $_.FullName + '"' })
    $taskEditorRsp = Join-Path $taskOutput 'editor.rsp'
    Set-Content -LiteralPath $taskEditorRsp -Value $taskEditorArgs -Encoding utf8
    & dotnet $taskCsc '-noconfig' ('@' + $taskEditorRsp)
    if ($LASTEXITCODE -ne 0) { throw '动作配表编辑器编译失败' }
    Write-Output '动作运行时与配表编辑器通过 Unity 2022.3 参考程序集编译。'
    exit 0
}

$taskValidationRoot = Join-Path $taskRepoRoot 'Temp/PlayerActionValidation'
New-Item -ItemType Directory -Path $taskValidationRoot -Force | Out-Null
foreach ($taskFolder in @('Assets', 'Packages', 'ProjectSettings')) {
    & robocopy (Join-Path $taskRepoRoot $taskFolder) (Join-Path $taskValidationRoot $taskFolder) /E /NJH /NJS /NDL /NFL /NP /R:1 /W:1 | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "复制失败：$taskFolder" }
}
if ($UseRepositoryPackages) {
    foreach ($taskPackageFile in @('manifest.json', 'packages-lock.json')) {
        $taskPackageContent = & git -C $taskRepoRoot show ('HEAD:Packages/' + $taskPackageFile)
        if ($LASTEXITCODE -ne 0) { throw '读取仓库依赖失败' }
        Set-Content -LiteralPath (Join-Path $taskValidationRoot ('Packages/' + $taskPackageFile)) -Value $taskPackageContent -Encoding utf8
    }
}
$taskCache = Join-Path $taskRepoRoot 'Library/PackageCache'
if (Test-Path -LiteralPath $taskCache) {
    & robocopy $taskCache (Join-Path $taskValidationRoot 'Library/PackageCache') /E /NJH /NJS /NDL /NFL /NP /R:1 /W:1 | Out-Null
    if ($LASTEXITCODE -ge 8) { throw '复制 PackageCache 失败' }
}
$taskLog = Join-Path $taskValidationRoot "$Stage.log"
if ($Stage -eq 'BaselinePlayMode') {
    $taskRuntimeFolder = Join-Path $taskValidationRoot 'Assets/Scripts/Actions'
    foreach ($taskSource in (Get-ChildItem -LiteralPath $taskRuntimeFolder -Filter '*.cs')) {
        $taskOriginal = Get-Content -LiteralPath $taskSource.FullName -Raw
        Set-Content -LiteralPath $taskSource.FullName -Encoding utf8 -Value ("#if ACTION_SEQUENCE_VALIDATION_INCLUDE`n" + $taskOriginal + "`n#endif")
    }
    foreach ($taskAssembly in @('Assets/Editor/Actions/GameJam.Actions.Editor.asmdef', 'Assets/Tests/Actions/Editor/GameJam.Actions.Tests.Editor.asmdef')) {
        $taskAssemblyPath = Join-Path $taskValidationRoot $taskAssembly
        $taskJson = Get-Content -LiteralPath $taskAssemblyPath -Raw | ConvertFrom-Json
        $taskJson | Add-Member -MemberType NoteProperty -Name defineConstraints -Value @('ACTION_SEQUENCE_VALIDATION_INCLUDE') -Force
        $taskJson | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $taskAssemblyPath -Encoding utf8
    }
}
$taskArguments = @('-batchmode', '-projectPath', ('"' + $taskValidationRoot + '"'), '-logFile', ('"' + $taskLog + '"'))
if ($Stage -ne 'EditMode') { $taskArguments += '-nographics' }
if ($Stage -eq 'Install') { $taskArguments += @('-executeMethod', 'GameJam.Actions.Editor.ActionPlayerSetup.Install', '-quit') }
elseif ($Stage -eq 'Examples') { $taskArguments += @('-executeMethod', 'GameJam.Actions.Editor.ActionSequenceBatchValidation.CreateAndCheckExamples', '-quit') }
else {
    $taskPlatform = if ($Stage -eq 'BaselinePlayMode') { 'PlayMode' } else { $Stage }
    $taskArguments += @('-runTests', '-testPlatform', $taskPlatform, '-testResults', ('"' + (Join-Path $taskValidationRoot "$Stage.xml") + '"'))
    if ($TestFilter) { $taskArguments += @('-testFilter', ('"' + $TestFilter + '"')) }
}
$taskProcess = Start-Process -FilePath $taskEditorPath -ArgumentList $taskArguments -WorkingDirectory $taskValidationRoot -WindowStyle Hidden -PassThru
Write-Output "Unity $Stage PID=$($taskProcess.Id)；日志：$taskLog"
$taskProcess.WaitForExit()
if ($taskProcess.ExitCode -ne 0) { throw "Unity $Stage 失败，exit=$($taskProcess.ExitCode)，请检查日志" }
if ($Stage -ne 'Examples' -and $Stage -ne 'Install') {
    $taskResult = Join-Path $taskValidationRoot "$Stage.xml"
    if (-not (Test-Path -LiteralPath $taskResult)) { throw 'Unity 未生成测试结果' }
    [xml]$taskXml = Get-Content -LiteralPath $taskResult -Raw
    if ($taskXml.'test-run'.result -ne 'Passed') { throw "测试未通过：$($taskXml.'test-run'.result)" }
    Write-Output "$Stage：$($taskXml.'test-run'.passed) 通过，$($taskXml.'test-run'.failed) 失败。"
}
elseif ($Stage -eq 'Install') { Write-Output '玩家攻击装配已生成在验证工程中。' }
else { Write-Output '独立示例资产与 SVG 已生成在验证工程中。' }
exit 0
