[CmdletBinding()]
param(
    [string]$PublicKey = '',
    [string]$PrivateKey = '',
    [string]$CngKeyName = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

throw 'This legacy key-generation entry point is retired and performs no action. Use Rotate-UpdateTrust.ps1 with an independently hashed NativeAOT ReleaseSigner.exe from a normal interactive maintainer account.'
