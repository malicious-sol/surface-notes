# Creates the Surface Notes signing key on this PC and helps you store it
# in the GitHub repository's secrets. Run once, in normal PowerShell:
#   irm https://raw.githubusercontent.com/malicious-sol/surface-notes/main/installer/make-signing-key.ps1 | iex
& {
    $ErrorActionPreference = 'Stop'
    $cert = New-SelfSignedCertificate -Type Custom -Subject 'CN=SurfaceNotes' -FriendlyName 'Surface Notes signing' `
        -KeyUsage DigitalSignature -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 `
        -KeyExportPolicy Exportable -NotAfter (Get-Date).AddYears(10) `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')

    $password = -join ((48..57) + (97..122) | Get-Random -Count 32 | ForEach-Object { [char]$_ })
    $pfx = [Convert]::ToBase64String($cert.Export('Pfx', $password))
    Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)" # the key now only lives in GitHub

    $url = 'https://github.com/malicious-sol/surface-notes/settings/secrets/actions/new'
    Start-Process $url

    Set-Clipboard -Value $pfx
    Write-Host ''
    Write-Host 'A GitHub page to add a secret just opened in your browser.' -ForegroundColor Green
    Write-Host 'Secret 1:  Name = SIGNING_PFX_BASE64   Secret = paste (Ctrl+V). It is on your clipboard now. Click Add secret.'
    Read-Host 'Press Enter here when secret 1 is saved'

    Start-Process $url
    Set-Clipboard -Value $password
    Write-Host 'Secret 2:  Name = SIGNING_PFX_PASSWORD   Secret = paste (Ctrl+V). It is on your clipboard now. Click Add secret.'
    Read-Host 'Press Enter here when secret 2 is saved'

    Set-Clipboard -Value ' '
    Write-Host 'Done. Tell Claude the secrets are in.' -ForegroundColor Green
}
