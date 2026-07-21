<#
.SYNOPSIS
    Reverts register-rewind-icon.ps1 — removes the .rewind file association
    and the RewindSnapshot.File ProgID from HKEY_CURRENT_USER.

.EXAMPLE
    .\unregister-rewind-icon.ps1
#>

$ErrorActionPreference = "Stop"

$progId = "RewindSnapshot.File"

if (Test-Path "HKCU:\Software\Classes\.rewind") {
    Remove-Item -Path "HKCU:\Software\Classes\.rewind" -Force
}

if (Test-Path "HKCU:\Software\Classes\$progId") {
    Remove-Item -Path "HKCU:\Software\Classes\$progId" -Recurse -Force
}

Stop-Process -Name explorer -Force
Start-Process explorer.exe

Write-Host "Removed .rewind file association."
