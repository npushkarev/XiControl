# SPDX-License-Identifier: GPL-3.0-only
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,
    [string]$VisualStudioPath
)
$ErrorActionPreference = 'Stop'

# Выход всегда снаружи source: в репозитории только исходники, не vendor DLL.
$source = [System.IO.Path]::GetFullPath($PSScriptRoot)
$output = [System.IO.Path]::GetFullPath($OutputDirectory)
$repositorySource = [System.IO.Path]::GetFullPath((Join-Path $source '..\..'))
if ($output.Equals($repositorySource, [System.StringComparison]::OrdinalIgnoreCase) -or
    $output.StartsWith($repositorySource + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputDirectory must be outside the source tree.'
}

if (-not $VisualStudioPath) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) {
        throw 'vswhere.exe not found; pass -VisualStudioPath for an installed MSVC toolchain.'
    }
    $VisualStudioPath = & $vswhere -latest -prerelease -products '*' `
        -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if ($LASTEXITCODE -ne 0 -or -not $VisualStudioPath) { throw 'Installed MSVC x64 tools not found.' }
}
$vcvars = Join-Path ([System.IO.Path]::GetFullPath($VisualStudioPath)) 'VC\Auxiliary\Build\vcvars64.bat'
if (-not (Test-Path -LiteralPath $vcvars -PathType Leaf)) { throw "vcvars64.bat not found: $vcvars" }
# cmd /c используется только для vcvars; не допускаем метасимволов в путях.
foreach ($path in @($source, $output, $vcvars)) {
    if ($path -match '["%&|<>^!\r\n]') { throw 'A build path contains unsupported cmd metacharacters.' }
}
New-Item -ItemType Directory -Path $output -Force | Out-Null
$dll = Join-Path $output 'XiIntelPower.dll'
$obj = Join-Path $output 'XiIntelPower.obj'
$pdb = Join-Path $output 'XiIntelPower.pdb'
$lib = Join-Path $output 'XiIntelPower.lib'
$cpp = Join-Path $source 'XiIntelPower.cpp'

# /DNDEBUG сохраняет WinVerifyTrust в Intel DllProxy. /MD — общий release CRT
# для std::string на границе vendor DLL; /MT и /MDd здесь запрещены.
$arguments = @(
    '/nologo', '/LD', '/std:c++17', '/EHsc', '/MD', '/O2', '/DNDEBUG',
    '/D_ITERATOR_DEBUG_LEVEL=0', '/D_DISABLE_CONSTEXPR_MUTEX_CONSTRUCTOR',
    '/DXI_INTEL_POWER_EXPORTS', '/W4', '/WX', '/GS', '/sdl', '/guard:cf', '/utf-8',
    ('/Fo"' + $obj + '"'), ('/Fd"' + $pdb + '"'), ('"' + $cpp + '"'),
    '/link', ('/OUT:"' + $dll + '"'), ('/IMPLIB:"' + $lib + '"'),
    'advapi32.lib', 'wintrust.lib', '/NXCOMPAT', '/DYNAMICBASE', '/HIGHENTROPYVA', '/CETCOMPAT', '/guard:cf', '/INCREMENTAL:NO'
)
$command = 'call "' + $vcvars + '" >nul && cl.exe ' + ($arguments -join ' ')
& $env:ComSpec /d /s /c $command
if ($LASTEXITCODE -ne 0) { throw "Native bridge build failed with exit code $LASTEXITCODE." }
Write-Output "Built user-mode bridge (not loaded): $dll"
