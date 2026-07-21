<#
.SYNOPSIS
    Registers a Windows Explorer icon for .rewind files (per-user, no admin rights required).

.DESCRIPTION
    Points HKEY_CURRENT_USER\Software\Classes\.rewind at a dedicated ProgID
    ("RewindSnapshot.File") and gives that ProgID a DefaultIcon pointing at
    Assets\icons\rewind-file.ico. This only changes the icon shown in Explorer —
    it does not set a default "open with" handler, so double-clicking a .rewind
    file still prompts you to choose an app (or does whatever it already did if
    the extension was previously associated with something else).

.EXAMPLE
    .\register-rewind-icon.ps1
#>

$ErrorActionPreference = "Stop"

$iconPath = Join-Path $PSScriptRoot "..\..\icons\rewind-file.ico"
$iconPath = (Resolve-Path $iconPath).Path
$progId = "RewindSnapshot.File"

New-Item -Path "HKCU:\Software\Classes\.rewind" -Force | Out-Null
Set-ItemProperty -Path "HKCU:\Software\Classes\.rewind" -Name "(Default)" -Value $progId

New-Item -Path "HKCU:\Software\Classes\$progId" -Force | Out-Null
Set-ItemProperty -Path "HKCU:\Software\Classes\$progId" -Name "(Default)" -Value "Rewind Snapshot"

New-Item -Path "HKCU:\Software\Classes\$progId\DefaultIcon" -Force | Out-Null
Set-ItemProperty -Path "HKCU:\Software\Classes\$progId\DefaultIcon" -Name "(Default)" -Value "$iconPath,0"

# Restart Explorer so the icon cache picks up the change immediately.
Stop-Process -Name explorer -Force
Start-Process explorer.exe

Write-Host "Registered .rewind file icon using $iconPath"
Write-Host "Run unregister-rewind-icon.ps1 to revert."
