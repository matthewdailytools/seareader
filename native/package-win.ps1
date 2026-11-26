param(
    [string]$Dotnet = 'dotnet',
    [string]$OutputDirectory,
    [string]$RestoreSource,
    [string]$VcRuntimeDirectory
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'SeaReader.Native/SeaReader.Native.csproj'
if (!$OutputDirectory) {
    $OutputDirectory = Join-Path (Split-Path $PSScriptRoot) 'release/windows-native'
}

$dotnetPath = (Get-Command $Dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$dotnetDirectory = Split-Path $dotnetPath
$desktopFramework = Join-Path $dotnetDirectory 'shared/Microsoft.WindowsDesktop.App'
$runtime = Get-ChildItem -LiteralPath $desktopFramework -Directory |
    Where-Object { $_.Name -match '^10\.\d+\.\d+$' -and
        (Test-Path (Join-Path $dotnetDirectory "shared/Microsoft.NETCore.App/$($_.Name)")) -and
        (Test-Path (Join-Path $dotnetDirectory "host/fxr/$($_.Name)")) } |
    Sort-Object { [Version]$_.Name } -Descending | Select-Object -First 1
if (!$runtime) { throw 'A matching .NET 10 Windows x64 desktop runtime is required in the SDK directory.' }

if (!$VcRuntimeDirectory) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (Test-Path -LiteralPath $vswhere) {
        $visualStudio = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Redist.14.Latest -property installationPath |
            Select-Object -First 1
        if ($visualStudio) {
            $redist = Get-ChildItem -LiteralPath (Join-Path $visualStudio 'VC/Redist/MSVC') -Directory |
                Where-Object { $_.Name -match '^\d+\.\d+\.\d+$' } |
                Sort-Object { [Version]$_.Name } -Descending | Select-Object -First 1
            if ($redist) { $VcRuntimeDirectory = Join-Path $redist.FullName 'x64/Microsoft.VC143.CRT' }
        }
    }
}
$ocrCrt = @('msvcp140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll')
$workerCrt = $ocrCrt + @('msvcp140_1.dll', 'msvcp140_atomic_wait.dll')
if (!$VcRuntimeDirectory) { throw 'Use -VcRuntimeDirectory or install the Visual C++ x64 redistributable build component.' }
foreach ($file in $workerCrt) {
    if (!(Test-Path -LiteralPath (Join-Path $VcRuntimeDirectory $file) -PathType Leaf)) {
        throw "Missing Visual C++ redistributable file: $file"
    }
}

$publishArguments = @(
    'publish', $project, '-c', 'Release', '-r', 'win-x64', '--no-self-contained',
    '-o', $OutputDirectory, '-p:AppHostDotNetSearch=AppRelative', '-p:AppHostRelativeDotNet=runtime',
    '-p:DebugType=None', '-p:DebugSymbols=false'
)
if ($RestoreSource) { $publishArguments += "-p:RestoreSources=$RestoreSource" }

& $dotnetPath @publishArguments
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
foreach ($file in $ocrCrt) {
    Copy-Item -LiteralPath (Join-Path $VcRuntimeDirectory $file) -Destination $OutputDirectory -Force
}

$nodeVersion = '26.13.1'
$nodeDigest = 'E2EDDA75BFB089654705CF65A13B58933BFD5F57148E8EBDA916F58A2474F1F7'
$nodeName = "node-v$nodeVersion-win-x64"
$cache = Join-Path $env:LOCALAPPDATA 'SeaReaderBuild'
$nodeArchive = Join-Path $cache "$nodeName.zip"
$nodeFolder = Join-Path $cache $nodeName
$nodeExe = Join-Path $nodeFolder 'node.exe'
New-Item -ItemType Directory -Path $cache -Force | Out-Null
if (!(Test-Path -LiteralPath $nodeArchive) -or (Get-FileHash -LiteralPath $nodeArchive).Hash -ne $nodeDigest) {
    $nodeDownloads = @(
        "https://nodejs.org/dist/v$nodeVersion/$nodeName.zip",
        "https://cdn.npmmirror.com/binaries/node/v$nodeVersion/$nodeName.zip"
    )
    $nodePartial = "$nodeArchive.$PID.part"
    try {
        foreach ($download in $nodeDownloads) {
            & curl.exe --fail --location --silent --show-error --max-time 90 -o $nodePartial $download
            if ($LASTEXITCODE -eq 0 -and (Get-FileHash -LiteralPath $nodePartial).Hash -eq $nodeDigest) {
                Move-Item -LiteralPath $nodePartial -Destination $nodeArchive -Force
                break
            }
        }
    } finally {
        if (Test-Path -LiteralPath $nodePartial) { Remove-Item -LiteralPath $nodePartial -Force }
    }
}
if (!(Test-Path -LiteralPath $nodeArchive) -or (Get-FileHash -LiteralPath $nodeArchive).Hash -ne $nodeDigest) {
    throw 'Node.js distribution checksum mismatch.'
}
Expand-Archive -LiteralPath $nodeArchive -DestinationPath $cache -Force
$workerDirectory = Join-Path $OutputDirectory 'local-worker'
$workerStage = Join-Path $PSScriptRoot 'worker/stage.mjs'
& $nodeExe $workerStage $workerDirectory
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Copy-Item -LiteralPath $nodeExe,(Join-Path $nodeFolder 'LICENSE') -Destination $workerDirectory -Force
foreach ($file in $workerCrt) {
    Copy-Item -LiteralPath (Join-Path $VcRuntimeDirectory $file) -Destination $workerDirectory -Force
}

$runtimeDirectory = Join-Path $OutputDirectory 'runtime'
foreach ($relative in @("host/fxr/$($runtime.Name)", "shared/Microsoft.NETCore.App/$($runtime.Name)",
                        "shared/Microsoft.WindowsDesktop.App/$($runtime.Name)")) {
    $destination = Join-Path $runtimeDirectory $relative
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Copy-Item -Path (Join-Path $dotnetDirectory "$relative/*") -Destination $destination -Recurse -Force
}
foreach ($notice in @('LICENSE.txt', 'ThirdPartyNotices.txt')) {
    Copy-Item -LiteralPath (Join-Path $dotnetDirectory $notice) -Destination $runtimeDirectory
}

$archive = "$OutputDirectory.zip"
Compress-Archive -Path (Join-Path $OutputDirectory '*') -DestinationPath $archive -CompressionLevel Optimal -Force
Get-Item -LiteralPath $archive
