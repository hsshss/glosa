# Builds the Windows release, publish\glosa-<version>-<rid>.zip (or in the folder -Out
# names).
#
#   tools\windows\package.ps1 [-Rid win-x64|win-arm64] [-Out <folder>] [-Version <version>]
#
# The runtime identifier defaults to this machine's, and the version to the project's.
# install.cmd and uninstall.cmd go at the top, and the scripts they start in libs\setup.
param(
    [string] $Rid,
    [string] $Out,
    [string] $Version
)

$ErrorActionPreference = 'Stop'

$here = $PSScriptRoot
$root = (Resolve-Path (Join-Path $here '..\..')).Path
if (-not $Rid) {
    $arm = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64'
    $Rid = if ($arm) { 'win-arm64' } else { 'win-x64' }
}
if (-not $Out) { $Out = Join-Path $root 'publish' }
$project = Join-Path $root 'src\Glosa.App\Glosa.App.csproj'
if (-not $Version) { $Version = (dotnet msbuild $project -getProperty:Version).Trim() }

$name = "glosa-$Version-$Rid"
$work = Join-Path ([IO.Path]::GetTempPath()) ([IO.Path]::GetRandomFileName())
$app = Join-Path $work $name
try {
    dotnet publish $project -c Release -r $Rid "-p:Version=$Version" -o $app
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    # Written out rather than copied, with CRLF whatever the checkout gave, and the
    # PowerShell scripts with a byte order mark.
    $withBom = New-Object Text.UTF8Encoding $true
    function Write-Dist([string] $source, [string] $target, [Text.Encoding] $encoding) {
        $text = [IO.File]::ReadAllText((Join-Path "$here\dist" $source)) -replace "`r?`n", "`r`n"
        [IO.File]::WriteAllText($target, $text, $encoding)
    }
    $setup = Join-Path $app 'libs\setup'
    New-Item -ItemType Directory -Force $setup | Out-Null
    Write-Dist 'install.cmd' (Join-Path $app 'install.cmd') ([Text.Encoding]::ASCII)
    Write-Dist 'uninstall.cmd' (Join-Path $app 'uninstall.cmd') ([Text.Encoding]::ASCII)
    Write-Dist 'install.ps1' (Join-Path $setup 'install.ps1') $withBom
    Write-Dist 'uninstall.ps1' (Join-Path $setup 'uninstall.ps1') $withBom

    New-Item -ItemType Directory -Force $Out | Out-Null
    $zip = Join-Path $Out "$name.zip"
    tar -a -c -f $zip -C $work $name
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    Write-Output $zip
}
finally {
    if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
}
