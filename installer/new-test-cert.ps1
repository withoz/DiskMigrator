# 코드 서명 파이프라인 "테스트용" 자체 서명 인증서를 만듭니다.
#
# ⚠️ 이 인증서는 오직 서명 파이프라인(build.ps1 → sign.ps1)이 동작하는지 확인하기 위한
#    것입니다. 자체 서명이라 SmartScreen·백신 신뢰를 전혀 주지 못합니다. 실제 배포에는
#    신뢰된 CA(OV/EV)의 인증서를 쓰십시오. (docs\CODE-SIGNING.md 참고)
#
# 하는 일:
#   1. CurrentUser\My 저장소에 코드 서명용 자체 서명 인증서를 만듭니다.
#   2. installer\test-cert.pfx 로 내보냅니다(암호 지정).
#   3. 서명에 쓸 환경변수 설정 방법을 안내합니다.
#
# 시스템 신뢰 저장소(Trusted Root)는 건드리지 않습니다 — 그래서 서명 후 검증은
# "신뢰되지 않음" 경고가 뜨며, 이는 자체 서명에서 정상입니다.

param([string]$Password = "test1234")

$ErrorActionPreference = 'Stop'
$pfxPath = Join-Path $PSScriptRoot "test-cert.pfx"

$cert = New-SelfSignedCertificate `
    -Type CodeSigningCert `
    -Subject "CN=DiskMigrator Test (DO NOT DISTRIBUTE)" `
    -CertStoreLocation "Cert:\CurrentUser\My" `
    -KeyExportPolicy Exportable `
    -KeySpec Signature `
    -KeyUsage DigitalSignature `
    -FriendlyName "DiskMigrator Test Signing" `
    -NotAfter (Get-Date).AddYears(3)

$sec = ConvertTo-SecureString -String $Password -Force -AsPlainText
Export-PfxCertificate -Cert "Cert:\CurrentUser\My\$($cert.Thumbprint)" -FilePath $pfxPath -Password $sec | Out-Null

Write-Host "테스트 인증서 생성:" -ForegroundColor Green
Write-Host "  PFX        : $pfxPath"
Write-Host "  Thumbprint : $($cert.Thumbprint)"
Write-Host ""
Write-Host "이 세션에서 서명 파이프라인을 테스트하려면:" -ForegroundColor Cyan
Write-Host "  `$env:DM_SIGN_PFX = '$pfxPath'"
Write-Host "  `$env:DM_SIGN_PFX_PASSWORD = '$Password'"
Write-Host "  .\build.ps1"
Write-Host ""
Write-Host "테스트가 끝나면 정리(선택):" -ForegroundColor Cyan
Write-Host "  Remove-Item '$pfxPath'"
Write-Host "  Remove-Item 'Cert:\CurrentUser\My\$($cert.Thumbprint)'"
