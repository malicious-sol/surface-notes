# Surface Notes one-time installer.
# Run in PowerShell opened as administrator:
#   irm {{SITE_URL}}/install.ps1 | iex
& {
    $ErrorActionPreference = 'Stop'
    $site = '{{SITE_URL}}'

    $me = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $me.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        Write-Host 'Please open PowerShell as administrator (right-click it > Run as administrator) and run this again.' -ForegroundColor Yellow
        return
    }

    $tmp = Join-Path $env:TEMP 'SurfaceNotesInstall'
    New-Item -ItemType Directory -Force $tmp | Out-Null

    Write-Host '1/4  Trusting the Surface Notes signing certificate...'
    Invoke-WebRequest "$site/SurfaceNotes.cer" -OutFile "$tmp\SurfaceNotes.cer" -UseBasicParsing
    Import-Certificate -FilePath "$tmp\SurfaceNotes.cer" -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null

    # A copy built with Visual Studio has to be removed first. Keep its notes.
    $backup = $null
    $old = Get-AppxPackage -Name 'SurfaceNotes.Local'
    if ($old -and $old.IsDevelopmentMode) {
        Write-Host '2/4  Saving notes from the Visual Studio copy and removing it...'
        $notes = Join-Path $env:LOCALAPPDATA "Packages\$($old.PackageFamilyName)\LocalState\Notebooks"
        if (Test-Path $notes) {
            $backup = Join-Path ([Environment]::GetFolderPath('MyDocuments')) ("SurfaceNotes backup " + (Get-Date -Format 'yyyy-MM-dd HHmm'))
            Copy-Item $notes $backup -Recurse
            Write-Host "     Backup saved to: $backup"
        }
        Remove-AppxPackage $old.PackageFullName
    } else {
        Write-Host '2/4  No Visual Studio copy to replace.'
    }

    Write-Host '3/4  Installing Surface Notes (this sets up automatic updates)...'
    Invoke-WebRequest "$site/SurfaceNotes.appinstaller" -OutFile "$tmp\SurfaceNotes.appinstaller" -UseBasicParsing
    Add-AppxPackage -AppInstallerFile "$tmp\SurfaceNotes.appinstaller"

    if ($backup) {
        Write-Host '4/4  Putting your notes back...'
        $pkg = Get-AppxPackage -Name 'SurfaceNotes.Local'
        $state = Join-Path $env:LOCALAPPDATA "Packages\$($pkg.PackageFamilyName)\LocalState"
        if (-not (Test-Path $state)) { New-Item -ItemType Directory -Force $state | Out-Null }
        Copy-Item $backup (Join-Path $state 'Notebooks') -Recurse -Force
    } else {
        Write-Host '4/4  Done.'
    }

    Write-Host ''
    Write-Host 'Surface Notes is installed. Open it from the Start menu.' -ForegroundColor Green
    Write-Host 'From now on it updates itself in the background whenever you open it.'
}
