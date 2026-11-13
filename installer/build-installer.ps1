<#
.SYNOPSIS
Builds ClearStart's installer.

.DESCRIPTION
1. Publishes the app for 64-bit Windows with its own copy of .NET, so the PC needs nothing else.
2. With -Sign, signs every program file in it that is not signed yet (ClearStart's own and the
   ManagedShell library; Microsoft's files already carry Microsoft's signature) and checks that
   every program file then has a valid signature.
3. Compiles installer\ClearStart.iss with Inno Setup 6. With -Sign, Inno Setup signs the setup
   program and its uninstaller as it builds them.
4. Checks the setup program's signature and writes its SHA-256 checksum next to it.

Signing uses Azure Artifact Signing through SignTool. The metadata file names the signing account
and certificate profile; its contents are never printed.

.PARAMETER Sign
Sign the app and the installer. Without it the installer is unsigned, which is fine for trying it
out but not for handing to anyone.

.PARAMETER MetadataPath
The Azure Artifact Signing metadata JSON. Defaults to the CLEARSTART_SIGNING_METADATA variable.

.EXAMPLE
installer\build-installer.ps1

.EXAMPLE
installer\build-installer.ps1 -Sign -MetadataPath C:\signing\azure-artifact-signing.json
#>
[CmdletBinding()]
param(
    [switch] $Sign,
    [string] $MetadataPath = $env:CLEARSTART_SIGNING_METADATA,
    [string] $SignToolPath = $env:SIGNTOOL_PATH,
    [string] $DlibPath = $env:AZURE_CODE_SIGNING_DLIB_PATH,
    [string] $IsccPath,
    [string] $TimestampUrl = 'http://timestamp.acs.microsoft.com/'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\ClearStart.csproj'
$script = Join-Path $PSScriptRoot 'ClearStart.iss'
$publish = Join-Path $root 'artifacts\publish\ClearStart-win-x64'
$output = Join-Path $root 'artifacts\installer'

function Find-File([string] $What, [string[]] $Candidates) {
    foreach ($candidate in $Candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }
    throw "$What was not found. Looked for: $(($Candidates | Where-Object { $_ }) -join '; ')"
}

function Get-ProgramFiles([string] $Folder) {
    Get-ChildItem -LiteralPath $Folder -Recurse -File | Where-Object { $_.Extension -in '.exe', '.dll' }
}

# ---------------------------------------------------------------- tools, before the long steps

$x86 = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
$iscc = Find-File 'Inno Setup 6 (ISCC.exe)' @(
    $IsccPath
    (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    (Join-Path $x86 'Inno Setup 6\ISCC.exe')
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
)

if ($Sign) {
    $kitSignTools = @()
    $kits = Join-Path $x86 'Windows Kits\10\bin'
    if (Test-Path -LiteralPath $kits) {
        $kitSignTools = Get-ChildItem -LiteralPath $kits -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
            Sort-Object FullName -Descending | ForEach-Object FullName
    }
    $signtool = Find-File 'SignTool' (@(
        $SignToolPath
        (Join-Path $x86 'Microsoft\ArtifactSigningClientTools\bin\signtool.exe')
        (Get-Command signtool.exe -ErrorAction SilentlyContinue).Source
    ) + $kitSignTools)
    $dlib = Find-File 'The Azure Artifact Signing library (Azure.CodeSigning.Dlib.dll)' @(
        $DlibPath
        (Join-Path $env:LOCALAPPDATA 'Microsoft\MicrosoftArtifactSigningClientTools\Azure.CodeSigning.Dlib.dll')
        (Join-Path $x86 'Microsoft\ArtifactSigningClientTools\bin\x64\Azure.CodeSigning.Dlib.dll')
        (Join-Path $x86 'Microsoft\ArtifactSigningClientTools\bin\Azure.CodeSigning.Dlib.dll')
    )
    if (-not $MetadataPath) {
        throw 'Signing needs the Azure Artifact Signing metadata file: pass -MetadataPath or set CLEARSTART_SIGNING_METADATA.'
    }
    $metadata = Find-File 'The signing metadata file' @($MetadataPath)
}

$version = ([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version |
    Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> in $project" }
Write-Host "ClearStart $version" -ForegroundColor Cyan

# ---------------------------------------------------------------- 1. publish

Write-Host 'Publishing the app with its own copy of .NET...'
if (Test-Path -LiteralPath $publish) { Remove-Item -LiteralPath $publish -Recurse -Force }
# ReadyToRun compiles the app ahead of time, so less of it is compiled while it starts at sign-in.
dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishReadyToRun=true -o $publish --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

# ---------------------------------------------------------------- 2. sign the app

if ($Sign) {
    $unsigned = @(Get-ProgramFiles $publish |
        Where-Object { (Get-AuthenticodeSignature -LiteralPath $_.FullName).Status -eq 'NotSigned' } |
        ForEach-Object FullName)
    Write-Host "Signing $($unsigned.Count) program files..."
    # The signing library prints the account it signs with; show that only when something goes wrong.
    # (Windows PowerShell turns a native program's error output into errors, so do not stop on those.)
    $ErrorActionPreference = 'Continue'
    $log = & $signtool sign /fd SHA256 /tr $TimestampUrl /td SHA256 /dlib $dlib /dmdf $metadata @unsigned 2>&1
    $ErrorActionPreference = 'Stop'
    if ($LASTEXITCODE -ne 0) {
        $log | ForEach-Object { Write-Host "$_" }
        throw "SignTool failed with exit code $LASTEXITCODE"
    }

    $bad = @(Get-ProgramFiles $publish | Where-Object {
        (Get-AuthenticodeSignature -LiteralPath $_.FullName).Status -ne 'Valid' })
    if ($bad.Count -gt 0) {
        throw "These files do not have a valid signature: $(($bad | ForEach-Object Name) -join ', ')"
    }
    Write-Host "Every program file in the app has a valid signature."
}

# ---------------------------------------------------------------- 3. compile the installer

Write-Host 'Compiling the installer...'
$arguments = @("/DAppVersion=$version", "/DPublishDir=$publish", "/O$output", '/Q')
if ($Sign) {
    # Inno Setup puts the file to sign in place of $f (quoted) and a quote in place of $q.
    $command = '$q' + $signtool + '$q sign /fd SHA256 /tr ' + $TimestampUrl + ' /td SHA256 /dlib $q' + $dlib +
        '$q /dmdf $q' + $metadata + '$q $f'
    $arguments += '/DSign', "/Sclearstart=$command"
}
& $iscc @arguments $script
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed with exit code $LASTEXITCODE" }

$setup = Join-Path $output "ClearStart-Setup-$version.exe"
if (-not (Test-Path -LiteralPath $setup)) { throw "Inno Setup did not write $setup" }

# ---------------------------------------------------------------- 4. check and checksum

$signer = $null
if ($Sign) {
    $null = & $signtool verify /pa /q $setup
    if ($LASTEXITCODE -ne 0) { throw "SignTool could not verify $setup" }
    $signature = Get-AuthenticodeSignature -LiteralPath $setup
    if ($signature.Status -ne 'Valid') { throw "The installer's signature is $($signature.Status)" }
    $signer = $signature.SignerCertificate.GetNameInfo('SimpleName', $false)
}
else {
    Write-Warning 'This installer is not signed. Windows will warn about it. Build with -Sign to sign it.'
}

$hash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
$checksum = "$setup.sha256.txt"
Set-Content -LiteralPath $checksum -Value "$hash  $(Split-Path $setup -Leaf)" -Encoding ascii

Write-Host ''
Write-Host "Installer: $setup ($([math]::Round((Get-Item -LiteralPath $setup).Length / 1MB, 1)) MB)" -ForegroundColor Green
Write-Host "Signed:    $(if ($signer) { "yes, by $signer" } else { 'no' })"
Write-Host "SHA-256:   $hash"
