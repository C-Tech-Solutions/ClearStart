<#
.SYNOPSIS
Publishes a built, signed installer as a GitHub release, in the shape ClearStart's updater expects.

.DESCRIPTION
Run installer\build-installer.ps1 -Sign first. This script then:
1. Checks the installer in artifacts\installer: ClearStart-Setup-<version>.exe must be the version in
   src\ClearStart.csproj, carry a valid signature from C-Tech Solutions LLC, and match the SHA-256 in
   ClearStart-Setup-<version>.exe.sha256.txt next to it.
2. Checks that the commit to release is on origin/main and that there is no release or tag for this
   version yet.
3. Creates the release v<version> on C-Tech-Solutions/ClearStart with gh, marked as the latest one, with
   those two files attached.
4. Reads the latest release back from GitHub's API, the way the updater will, and checks it.

The updater (src\Updates.cs) reads releases/latest, which leaves out drafts and pre-releases. It wants a
tag of the form v1.2.3, and exactly these two files attached to the release:
  ClearStart-Setup-1.2.3.exe
  ClearStart-Setup-1.2.3.exe.sha256.txt   (one line: "<sha256 in hex>  ClearStart-Setup-1.2.3.exe")
Every copy of ClearStart that checks for updates sees a release the moment it is published.

.PARAMETER NotesFile
A Markdown file with the release notes. Without one, GitHub writes notes from the merged pull requests.

.PARAMETER Target
The commit to tag. Defaults to the commit checked out here, which has to be on origin/main.

.EXAMPLE
installer\publish-release.ps1 -WhatIf

.EXAMPLE
installer\publish-release.ps1 -NotesFile artifacts\notes-0.2.0.md
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $NotesFile,
    [string] $Target
)

$ErrorActionPreference = 'Stop'
$repository = 'C-Tech-Solutions/ClearStart'
$publisher = 'C-Tech Solutions LLC'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\ClearStart.csproj'
$output = Join-Path $root 'artifacts\installer'

function Invoke-Native([string] $What, [scriptblock] $Command) {
    # Windows PowerShell turns a native program's error output into errors, so do not stop on those.
    $ErrorActionPreference = 'Continue'
    $result = & $Command 2>&1
    $code = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    if ($code -ne 0) {
        $result | ForEach-Object { Write-Host "$_" }
        throw "$What failed with exit code $code"
    }
    return $result
}

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'The GitHub CLI (gh) was not found.' }
if (-not (Get-Command git -ErrorAction SilentlyContinue)) { throw 'git was not found.' }

# ---------------------------------------------------------------- 1. the installer

$version = ([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version |
    Where-Object { $_ } | Select-Object -First 1
if ($version -notmatch '^\d{1,6}\.\d{1,6}\.\d{1,6}$') {
    throw "The version in $project is '$version'. The updater only reads versions like 1.2.3."
}
$tag = "v$version"
$setupName = "ClearStart-Setup-$version.exe"
$setup = Join-Path $output $setupName
$checksum = "$setup.sha256.txt"
Write-Host "ClearStart $version" -ForegroundColor Cyan

foreach ($file in $setup, $checksum) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
        throw "$file is missing. Build it with installer\build-installer.ps1 -Sign."
    }
}

$fileVersion = "$((Get-Item -LiteralPath $setup).VersionInfo.FileVersion)".Trim()
if (-not $fileVersion -or ([version]$fileVersion).ToString(3) -ne $version) {
    throw "$setupName says it is version '$fileVersion', not $version. Build the installer again."
}

$signature = Get-AuthenticodeSignature -LiteralPath $setup
if ($signature.Status -ne 'Valid') { throw "$setupName's signature is $($signature.Status): $($signature.StatusMessage)" }
$signer = $signature.SignerCertificate.GetNameInfo('SimpleName', $false)
if ($signer -ne $publisher) { throw "$setupName is signed by '$signer', not by $publisher." }
# The updater accepts a Setup only from the publisher that signed the copy it runs in.
$installed = Join-Path $env:LOCALAPPDATA 'Programs\ClearStart\ClearStart.exe'
if (Test-Path -LiteralPath $installed) {
    $current = Get-AuthenticodeSignature -LiteralPath $installed
    if ($current.Status -eq 'Valid' -and $current.SignerCertificate.Subject -ne $signature.SignerCertificate.Subject) {
        Write-Warning ("The installed ClearStart is signed as '$($current.SignerCertificate.Subject)', this " +
            "installer as '$($signature.SignerCertificate.Subject)'. Copies signed the old way will refuse this update.")
    }
}

# Not Get-FileHash: in a dry run (-WhatIf), Windows PowerShell's Get-FileHash hashes nothing and returns null.
$sha = [Security.Cryptography.SHA256]::Create()
$stream = [IO.File]::OpenRead($setup)
try { $hash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
finally { $stream.Dispose(); $sha.Dispose() }
$listed = (Get-Content -LiteralPath $checksum -Raw).Trim()
if ($listed -ne "$hash  $setupName") {
    throw "$checksum does not match the installer. It should read: $hash  $setupName"
}
Write-Host "Installer: $setupName, signed by $signer, SHA-256 $hash"

# ---------------------------------------------------------------- 2. the commit and the tag

Invoke-Native 'git fetch' { git -C $root fetch --quiet --tags origin main } | Out-Null
if (-not $Target) { $Target = (Invoke-Native 'git rev-parse' { git -C $root rev-parse HEAD }) | Select-Object -First 1 }
$Target = (Invoke-Native 'git rev-parse' { git -C $root rev-parse --verify "$Target^{commit}" }) | Select-Object -First 1
# The tag has to point at the source of this version, not just at some commit on main.
$targetProject = (Invoke-Native 'git show' { git -C $root show "${Target}:src/ClearStart.csproj" }) -join "`n"
$targetVersion = ([xml]$targetProject).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if ($targetVersion -ne $version) {
    throw "Commit $Target is version '$targetVersion', not $version. Release the commit the installer was built from."
}
$ErrorActionPreference = 'Continue'
git -C $root merge-base --is-ancestor $Target origin/main 2>&1 | Out-Null
$onMain = $LASTEXITCODE -eq 0
git -C $root rev-parse --verify --quiet "refs/tags/$tag" 2>&1 | Out-Null
$tagExists = $LASTEXITCODE -eq 0
gh release view $tag --repo $repository 2>&1 | Out-Null
$releaseExists = $LASTEXITCODE -eq 0
$ErrorActionPreference = 'Stop'

if (-not $onMain) { throw "Commit $Target is not on origin/main. Release from main." }
if ($tagExists) { throw "The tag $tag already exists. Raise <Version> in $project for a new release." }
if ($releaseExists) { throw "There is already a release $tag on $repository." }
if ($NotesFile -and -not (Test-Path -LiteralPath $NotesFile -PathType Leaf)) { throw "$NotesFile was not found." }
Write-Host "Commit:    $Target (on origin/main)"

# ---------------------------------------------------------------- 3. the release

$arguments = @('release', 'create', $tag, $setup, $checksum, '--repo', $repository, '--target', $Target,
    '--title', "ClearStart $version", '--latest')
if ($NotesFile) { $arguments += '--notes-file', $NotesFile } else { $arguments += '--generate-notes' }

if (-not $PSCmdlet.ShouldProcess("$repository", "Publish release $tag with $setupName and its checksum")) {
    Write-Host "Would run: gh $($arguments -join ' ')"
    return
}
Invoke-Native 'gh release create' { gh @arguments } | ForEach-Object { Write-Host "$_" }

# ---------------------------------------------------------------- 4. read it back as the updater will

$latest = (Invoke-Native 'gh api' { gh api "repos/$repository/releases/latest" }) -join "`n" | ConvertFrom-Json
$problems = @()
if ($latest.tag_name -ne $tag) { $problems += "the latest release is $($latest.tag_name), not $tag" }
if ($latest.draft -or $latest.prerelease) { $problems += 'it is a draft or a pre-release' }
foreach ($expected in @(@{ Name = $setupName; Path = $setup }, @{ Name = "$setupName.sha256.txt"; Path = $checksum })) {
    $asset = $latest.assets | Where-Object { $_.name -eq $expected.Name } | Select-Object -First 1
    $prefix = "https://github.com/$repository/releases/download/"
    if (-not $asset) { $problems += "$($expected.Name) is not attached"; continue }
    if ($asset.size -ne (Get-Item -LiteralPath $expected.Path).Length) { $problems += "$($expected.Name) is not the size it should be" }
    if (-not $asset.browser_download_url.StartsWith($prefix)) { $problems += "$($expected.Name) downloads from $($asset.browser_download_url)" }
}
if ($problems.Count -gt 0) { throw "The release is up, but the updater will not take it: $($problems -join '; ')" }

Write-Host ''
Write-Host "Published $($latest.html_url)" -ForegroundColor Green
Write-Host 'Copies of ClearStart that check for updates will offer it within a day.'
