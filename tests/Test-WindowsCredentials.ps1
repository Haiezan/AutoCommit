# Run with Windows PowerShell after building the Release VSIX.
$ErrorActionPreference = 'Stop'
$assemblyPath = Join-Path $PSScriptRoot '..\src\AutoCommit.Vsix\bin\Release\net472\AutoCommit.Vsix.dll'
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path $assemblyPath))
$type = $assembly.GetType('AutoCommit.Vsix.CredentialStore', $true)
$scopeType = $assembly.GetType('AutoCommit.Vsix.KeyScope', $true)
$flags = [Reflection.BindingFlags]'Instance,NonPublic'
$directory = Join-Path ([IO.Path]::GetTempPath()) ('AutoCommit-KeyTest-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($directory)
$store = $type.GetConstructors($flags)[0].Invoke(@([string]$directory))
$first = Join-Path $directory 'Source'
$second = Join-Path $first 'Example'
$address = 'https://example.test/v1'
function Save-Key($root, $url, $mode, $value) {
    $type.GetMethod('Save', $flags).Invoke($store, @([string]$root, [string]$url, [Enum]::Parse($scopeType, $mode), $value))
}
function Check-Key($root, $url, $mode, $expected) {
    $arguments = [object[]]@([string]$root, [string]$url, $null)
    $value = $type.GetMethod('Load', $flags).Invoke($store, $arguments)
    if ($value -cne $expected -or $arguments[2].ToString() -ne $mode) { throw "Credential mismatch: $mode" }
}
try {
    Add-Type -AssemblyName System.Security
    $legacy = Join-Path $directory 'key.dat'
    $cipher = [Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes('legacy'), $null, 'CurrentUser')
    [IO.File]::WriteAllBytes($legacy, $cipher)
    $type.GetMethod('MigrateLegacyKey', $flags).Invoke($store, @($address))
    Check-Key $first $address Shared 'legacy'
    Save-Key '' $address Shared 'shared'
    Check-Key $first ($address + '/') Shared 'shared'
    Check-Key $second $address Shared 'shared'
    Check-Key $first ($address + '/other') Shared ''
    Save-Key $first $address Project 'local'
    Check-Key $first $address Project 'local'
    Check-Key $second $address Shared 'shared'
    $projectFile = Get-ChildItem (Join-Path $directory 'credentials') -File -Filter '*.dat'
    [IO.File]::WriteAllBytes($projectFile.FullName, [byte[]](1,2,3))
    $failedDecrypt = $false
    try { Check-Key $first $address Project 'local' }
    catch { $failedDecrypt = $_.Exception.GetBaseException() -is [Security.Cryptography.CryptographicException] }
    if (!$failedDecrypt) { throw 'Corrupt override must report a decryption error.' }
    Save-Key $first $address Anonymous ''
    Check-Key $first $address Anonymous ''
    $type.GetMethod('UseShared', $flags).Invoke($store, @([string]$first, $address))
    Check-Key $first $address Shared 'shared'
    Save-Key $first $address Project ''
    Check-Key $first $address Shared 'shared'
    [IO.File]::WriteAllBytes($legacy, $cipher)
    $type.GetMethod('MigrateLegacyKey', $flags).Invoke($store, @($null))
    if (Test-Path $legacy) { throw 'Unbound legacy credentials must not follow a new address.' }
    Check-Key $first ($address + '/new') Shared ''
    Write-Output 'Windows DPAPI credential checks passed.'
} finally {
    $target = (Resolve-Path -LiteralPath $directory).Path
    if ([IO.Path]::GetDirectoryName($target) -eq [IO.Path]::GetTempPath().TrimEnd('\') -and
        [IO.Path]::GetFileName($target) -match '^AutoCommit-KeyTest-[a-f0-9]{32}$') {
        Remove-Item -LiteralPath $target -Recurse -Force
    }
}
