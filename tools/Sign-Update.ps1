# Copyright (c) 2026 Jason1993余. See LICENSE.
# Run on the author's Windows computer. The private key must stay outside the repository.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$PackagePath,
    [Parameter(Mandatory=$true)][string]$SigningKeyPath,
    [Parameter(Mandatory=$true)][string]$Version,
    [Parameter(Mandatory=$true)][ValidateRange(2,2147483646)][int]$ReleaseSequence,
    [ValidateRange(1,2147483646)][int]$MinimumSupportedSequence = 1,
    [string]$NotesPath,
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\update\stable.json')
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\','/')
$package = (Resolve-Path -LiteralPath $PackagePath).Path
$privateKey = (Resolve-Path -LiteralPath $SigningKeyPath).Path
if ($privateKey.StartsWith($repo + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Keep the private signing key OUTSIDE the source repository.'
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must have three numeric parts, for example 1.1.0.' }
if ($MinimumSupportedSequence -gt $ReleaseSequence) { throw 'MinimumSupportedSequence cannot exceed ReleaseSequence.' }
$source = [IO.File]::ReadAllText((Join-Path $repo 'src\UpdateTrust.cs'))
if ($source -notmatch ('Version\s*=\s*"' + [regex]::Escape($Version) + '"') -or
    $source -notmatch ('ReleaseSequence\s*=\s*' + $ReleaseSequence + '\s*;')) {
    throw 'First update src/UpdateTrust.cs and the assembly/manifest version, then rebuild the application.'
}
$publicMatch = [regex]::Match($source, 'PublicKeyXml\s*=\s*"([^"]+)"')
if (-not $publicMatch.Success) { throw 'Embedded update public key was not found.' }
$name = [IO.Path]::GetFileName($package)
if ($name -notmatch '^[A-Za-z0-9_.-]+\.zip$') { throw 'Use an ASCII .zip package filename without spaces.' }
$length = (Get-Item -LiteralPath $package).Length
if ($length -le 0 -or $length -gt 100MB) { throw 'Patch archive must be between 1 byte and 100 MiB.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($package)
try {
    $entryNames = @($archive.Entries | ForEach-Object { $_.FullName })
    if ($entryNames -cnotcontains 'ADB_APK_Tool.exe' -or $entryNames -cnotcontains 'JasonUpdater.exe') {
        throw 'Use a flat Patch ZIP containing both ADB_APK_Tool.exe and JasonUpdater.exe at its root.'
    }
    $allowed = @('ADB_APK_Tool.exe','JasonUpdater.exe','ADB_APK_Tool.exe.config','JasonUpdater.exe.config',
        'LICENSE','README.md','README-zh-TW.txt','PHONE_SETUP-zh-TW.txt','CHANGELOG.md','RELEASE_NOTES.md',
        'UPDATES-zh-TW.txt','UPDATE_OWNER_GUIDE.md','OneClickInstall.cmd','一鍵安裝.cmd',
        'phone-guides/Common.txt','phone-guides/Google.txt','phone-guides/Samsung.txt','phone-guides/Sony.txt','phone-guides/Xiaomi.txt',
        'docs/UPDATES.md','docs/COMMERCIAL-ROADMAP.md','update/README.md')
    $seen = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    [long]$expanded = 0
    if ($archive.Entries.Count -gt 100) { throw 'Patch has too many entries.' }
    foreach ($item in $archive.Entries) {
        $entryName = $item.FullName
        if (-not $seen.Add($entryName)) { throw 'Patch has duplicate paths.' }
        if (@('phone-guides/','components/','docs/','update/') -ccontains $entryName) {
            if ($item.Length -ne 0) { throw 'Directory entries cannot contain data.' }
            continue
        }
        if ($entryName.Contains('..') -or ($allowed -cnotcontains $entryName -and $entryName -cnotmatch '^components/[A-Za-z0-9][A-Za-z0-9_.-]*\.dll$')) {
            throw ('Patch contains an unexpected path: ' + $entryName)
        }
        if ([IO.Path]::GetFileNameWithoutExtension($entryName) -match '^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])($|\.)') {
            throw 'Patch cannot use Windows reserved filenames.'
        }
        $expanded += $item.Length
        if ($item.Length -gt 100MB -or $expanded -gt 200MB) { throw 'Expanded patch is too large.' }
        $mode = ($item.ExternalAttributes -shr 16) -band 0xF000
        if (($mode -ne 0 -and $mode -ne 0x8000) -or ($item.ExternalAttributes -band 0x400) -ne 0) {
            throw 'Patch cannot contain symbolic links or special files.'
        }
    }
    foreach ($exeName in @('ADB_APK_Tool.exe','JasonUpdater.exe')) {
        $entry = $archive.GetEntry($exeName)
        $tempExe = Join-Path ([IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString('N') + '.exe')
        try {
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $tempExe)
            $assemblyVersion = [Reflection.AssemblyName]::GetAssemblyName($tempExe).Version
            if ($assemblyVersion -ne [Version]($Version + '.0')) { throw ('Patch EXE version does not match Version: ' + $exeName) }
        } finally { if (Test-Path -LiteralPath $tempExe) { Remove-Item -LiteralPath $tempExe -Force } }
    }
} finally { $archive.Dispose() }
$notes = if ($NotesPath) { [IO.File]::ReadAllText((Resolve-Path -LiteralPath $NotesPath).Path) } else { 'New release by Jason1993余.' }
if ($notes.Length -gt 6000) { throw 'Release notes must be 6000 characters or fewer.' }
$csp = New-Object System.Security.Cryptography.CspParameters(24)
$rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider(3072, $csp)
$rsa.PersistKeyInCsp = $false
try {
    $rsa.FromXmlString([IO.File]::ReadAllText($privateKey))
    if ($rsa.PublicOnly -or $rsa.KeySize -lt 3072) { throw 'A private RSA key of at least 3072 bits is required.' }
    if ($rsa.ToXmlString($false) -cne $publicMatch.Groups[1].Value) {
        throw 'Signing key does not match the trusted public key embedded in the application.'
    }
    $payload = [ordered]@{
        schema = 1
        appId = 'jason-adb-apk-installer'
        channel = 'stable'
        version = $Version
        releaseSequence = $ReleaseSequence
        minimumSupportedSequence = $MinimumSupportedSequence
        publishedUtc = [DateTime]::UtcNow.ToString('o')
        notes = $notes
        packageUrl = 'https://github.com/qaz754120/jason-adb-apk-installer/releases/download/v' + $Version + '/' + $name
        packageSize = $length
        packageSha256 = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    $previousFeed = Join-Path $repo 'update\stable.json'
    if (Test-Path -LiteralPath $previousFeed) {
        $oldEnvelope = [IO.File]::ReadAllText($previousFeed) | ConvertFrom-Json
        $oldBytes = [Convert]::FromBase64String($oldEnvelope.payload)
        if (-not $rsa.VerifyData($oldBytes, 'SHA256', [Convert]::FromBase64String($oldEnvelope.signature))) {
            throw 'Existing feed signature is invalid. Fetch the last authentic feed before publishing.'
        }
        $old = [Text.Encoding]::UTF8.GetString($oldBytes) | ConvertFrom-Json
        if ($old.appId -cne $payload.appId -or $old.channel -cne 'stable') { throw 'Existing feed belongs to another application or channel.' }
        if ($ReleaseSequence -lt $old.releaseSequence -or $MinimumSupportedSequence -lt $old.minimumSupportedSequence) {
            throw 'Never decrease the release sequence or minimum supported sequence.'
        }
        if ($ReleaseSequence -eq $old.releaseSequence) {
            if ($Version -cne $old.version -or $payload.packageSha256 -cne $old.packageSha256 -or
                $length -ne $old.packageSize -or $payload.packageUrl -cne $old.packageUrl) {
                throw 'Never replace a published package under the same release sequence. Publish a new version and sequence.'
            }
        } elseif ($ReleaseSequence -ne ($old.releaseSequence + 1) -or [Version]$Version -le [Version]$old.version) {
            throw 'A new stable release must increase the sequence by one and use a higher version number.'
        }
    }
    $utf8 = New-Object Text.UTF8Encoding($false)
    $bytes = $utf8.GetBytes(($payload | ConvertTo-Json -Compress))
    $signature = $rsa.SignData($bytes, 'SHA256')
    if (-not $rsa.VerifyData($bytes, 'SHA256', $signature)) { throw 'Self-verification of the manifest signature failed.' }
    $envelope = [ordered]@{
        payload = [Convert]::ToBase64String($bytes)
        signature = [Convert]::ToBase64String($signature)
    } | ConvertTo-Json -Compress
    $destination = [IO.Path]::GetFullPath($OutputPath)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
    [IO.File]::WriteAllText($destination, $envelope + "`n", $utf8)
    Write-Host ('Signed manifest: ' + $destination)
    Write-Host ('Release sequence: ' + $ReleaseSequence + '; package SHA-256: ' + $payload.packageSha256)
    Write-Host 'Upload the ZIP to its GitHub Release and verify it first. Publish update/stable.json LAST.'
} finally { $rsa.Dispose() }
