# DiskMigrator 코드 서명 스크립트
#
# 주어진 파일(들)에 Authenticode 서명을 붙입니다. 인증서 정보는 저장소에 두지 않고
# 환경변수로 받습니다(비밀이 커밋되지 않게).
#
# 설정 (아래 중 하나로 인증서 지정):
#   DM_SIGN_PFX            .pfx 파일 경로
#   DM_SIGN_PFX_PASSWORD   .pfx 암호 (선택)
#   -- 또는 --
#   DM_SIGN_THUMBPRINT     Windows 인증서 저장소에 설치된 서명 인증서의 지문(thumbprint)
#
#   DM_SIGN_TIMESTAMP_URL  타임스탬프 서버 (기본: http://timestamp.digicert.com)
#                          타임스탬프가 있어야 인증서 만료 후에도 서명이 유효합니다.
#
# 인증서가 설정되지 않으면: 서명을 "건너뜀"으로 표시하고 그대로 통과합니다
# (-Required 를 주면 그때는 오류로 중단). 그래서 인증서 없이도 빌드는 됩니다.
#
# 반환: 서명하면 $true, 인증서 미설정으로 건너뛰면 $false. 서명 실패는 throw.

param(
    [Parameter(Mandatory = $true)][string[]]$Files,
    [switch]$Required
)

$ErrorActionPreference = 'Stop'

function Find-SignTool {
    # 있으면 PATH의 signtool을 먼저.
    $cmd = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    # 없으면 Windows SDK에서 최신 x64 signtool을 찾습니다.
    $bin = "C:\Program Files (x86)\Windows Kits\10\bin"
    if (Test-Path $bin) {
        $st = Get-ChildItem $bin -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
              Where-Object { $_.FullName -match '\\x64\\' } |
              Sort-Object FullName -Descending | Select-Object -First 1
        if ($st) { return $st.FullName }
    }
    throw "signtool.exe를 찾지 못했습니다. Windows SDK를 설치하십시오 ('winget install Microsoft.WindowsSDK')."
}

$pfx        = $env:DM_SIGN_PFX
$pfxPass    = $env:DM_SIGN_PFX_PASSWORD
$thumbprint = $env:DM_SIGN_THUMBPRINT
$tsUrl      = if ($env:DM_SIGN_TIMESTAMP_URL) { $env:DM_SIGN_TIMESTAMP_URL } else { "http://timestamp.digicert.com" }

if (-not $pfx -and -not $thumbprint) {
    $msg = "코드 서명 인증서가 설정되지 않았습니다(DM_SIGN_PFX 또는 DM_SIGN_THUMBPRINT). 서명을 건너뜁니다."
    if ($Required) { throw $msg }
    Write-Warning $msg
    Write-Warning "서명 없는 빌드는 실행 시 SmartScreen 경고가 뜹니다. 배포 전 서명을 권장합니다."
    return $false
}

$signtool = Find-SignTool

# 공통 인자: SHA-256 파일 다이제스트 + RFC3161 타임스탬프.
$common = @('/fd', 'sha256', '/tr', $tsUrl, '/td', 'sha256')
if ($pfx) {
    if (-not (Test-Path $pfx)) { throw "DM_SIGN_PFX 경로를 찾을 수 없습니다: $pfx" }
    $certArgs = @('/f', $pfx)
    if ($pfxPass) { $certArgs += @('/p', $pfxPass) }
}
else {
    $certArgs = @('/sha1', $thumbprint)
}

foreach ($file in $Files) {
    if (-not (Test-Path $file)) { throw "서명할 파일을 찾을 수 없습니다: $file" }
    Write-Host "서명: $file" -ForegroundColor Cyan
    & $signtool sign @certArgs @common $file
    if ($LASTEXITCODE -ne 0) { throw "signtool sign 실패 (exit $LASTEXITCODE): $file" }

    & $signtool verify /pa $file
    if ($LASTEXITCODE -ne 0) {
        # /pa 검증 실패: 서명 자체는 붙었으나 인증서 체인이 신뢰되지 않을 수 있습니다
        # (자체 서명 테스트 인증서가 대표적). 서명이 아예 없는 것과는 구분합니다.
        $sig = Get-AuthenticodeSignature $file
        if ($sig.SignerCertificate) {
            Write-Warning "서명은 붙었으나 인증서 체인이 신뢰되지 않습니다 (서명자: $($sig.SignerCertificate.Subject))."
            Write-Warning "자체 서명 테스트 인증서라면 정상입니다. 배포용은 신뢰된 CA 인증서를 쓰십시오."
        }
        else {
            throw "서명 검증 실패 — 서명이 붙지 않았습니다: $file"
        }
    }
}

Write-Host "서명 완료 ($($Files.Count)개 파일)." -ForegroundColor Green
return $true
