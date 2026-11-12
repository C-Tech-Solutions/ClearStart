<# Builds and stages the optional startup components. Does not run, register or enable any of them. #>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$nativeBuild = Join-Path $repo 'artifacts\native'
$stage = Join-Path $repo 'artifacts\early-startup'
cmake -S $PSScriptRoot -B $nativeBuild -G 'Visual Studio 17 2022' -A x64
if ($LASTEXITCODE -ne 0) { throw 'CMake configuration failed' }
cmake --build $nativeBuild --config Release
if ($LASTEXITCODE -ne 0) { throw 'Native build failed' }
dotnet publish (Join-Path $repo 'src\ClearStart.csproj') -c Release -r win-x64 --self-contained true -o $stage
if ($LASTEXITCODE -ne 0) { throw 'ClearStart publish failed' }
foreach ($name in @('ClearStart.Loader.exe', 'ClearStart.ExplorerGuard.dll')) {
    Copy-Item -LiteralPath (Join-Path $nativeBuild "Release\$name") -Destination $stage
}
Copy-Item -LiteralPath (Join-Path $nativeBuild 'Detours-LICENSE.txt') -Destination $stage
Write-Host "Built the disabled early-startup candidate in $stage"
