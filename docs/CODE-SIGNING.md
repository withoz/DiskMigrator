# 코드 서명 (Code Signing)

DiskMigrator를 배포하려면 **코드 서명**이 사실상 필수입니다. 서명이 없으면:

- 사용자가 실행할 때 **SmartScreen "Windows가 PC를 보호했습니다"** 빨간 경고가 뜨고,
- 백신이 **오탐**으로 차단할 가능성이 높습니다.

특히 이 앱은 **관리자 권한 + 저수준 디스크/VSS 접근**을 해서 서명 없는 상태의 마찰이 큽니다.

이 저장소에는 **인증서가 준비되면 빌드가 자동으로 서명**하는 파이프라인이 이미 들어 있습니다.
인증서만 구하면 됩니다.

---

## 1. 파이프라인 사용법 (인증서가 있을 때)

`installer\build.ps1` 은 아래 환경변수가 있으면 **앱 exe와 설치 exe를 모두 서명**합니다.
없으면 경고만 남기고 서명 없이 빌드합니다.

### 방법 A — PFX 파일

```powershell
$env:DM_SIGN_PFX = "C:\경로\인증서.pfx"
$env:DM_SIGN_PFX_PASSWORD = "PFX암호"      # 선택
.\installer\build.ps1
```

### 방법 B — Windows 인증서 저장소 (지문)

인증서를 저장소에 설치했다면 지문(thumbprint)만 지정합니다.

```powershell
$env:DM_SIGN_THUMBPRINT = "A1B2C3...지문"
.\installer\build.ps1
```

### 공통 — 타임스탬프

기본 타임스탬프 서버는 DigiCert입니다. 타임스탬프가 있어야 **인증서 만료 후에도 서명이
유효**합니다. 바꾸려면:

```powershell
$env:DM_SIGN_TIMESTAMP_URL = "http://timestamp.sectigo.com"
```

서명 도구는 Windows SDK의 `signtool.exe` 를 자동으로 찾습니다. 없으면
`winget install Microsoft.WindowsSDK` 로 설치하십시오.

---

## 2. 인증서 종류 — 무엇을 살 것인가

| 종류 | SmartScreen | 키 보관 | 대략 비용(연) | 비고 |
|---|---|---|---|---|
| **OV** (조직 인증) | 평판을 **쌓아야** 경고가 사라짐 | HSM/USB 토큰 또는 클라우드 | 20~40만원대 | 다운로드가 쌓이면 개선 |
| **EV** (확장 인증) | **즉시** 경고 없음 | 하드웨어 토큰/클라우드 HSM 필수 | 40~90만원대 | 가장 확실 |
| 개인/자체 서명 | 도움 안 됨 | — | — | 파이프라인 **테스트용**만 |

> **2023년 이후 중요 변화**: 공개 신뢰 코드 서명 인증서(OV·EV)는 개인 키를 반드시
> **하드웨어 토큰이나 클라우드 HSM**에 보관해야 합니다. 예전처럼 평범한 `.pfx` 파일로 발급되는
> 공개 OV 인증서는 사실상 사라졌습니다. 따라서 실무에서는 아래 "클라우드 서명"을 주로 씁니다.

### 발급 기관(CA) 예시
Sectigo, DigiCert, GlobalSign, SSL.com, Certum(개인 개발자용 저가 옵션) 등.

---

## 3. 클라우드 HSM 서명 (요즘 표준)

키가 클라우드 HSM에 있으면 `.pfx`가 없으므로 전용 도구로 서명합니다. 대표적으로:

- **Azure Key Vault** + [`AzureSignTool`](https://github.com/vcsjones/AzureSignTool)
- **DigiCert KeyLocker** (`smctl`)
- **SSL.com eSigner** (`CodeSignTool`)

이 경우 `sign.ps1` 대신 해당 도구로 서명하도록 바꾸면 됩니다. 예 (Azure Key Vault):

```powershell
AzureSignTool sign `
  -kvu https://<금고>.vault.azure.net -kvc <인증서이름> `
  -kvi <appId> -kvs <secret> -kvt <tenant> `
  -tr http://timestamp.digicert.com -td sha256 -fd sha256 `
  "설치exe경로"
```

필요하면 이 방식으로 `sign.ps1`을 교체해 드릴 수 있습니다.

---

## 4. 파이프라인 동작 테스트 (자체 서명)

인증서를 사기 전에 **서명 과정이 제대로 도는지**만 확인하려면 자체 서명 인증서를 씁니다.

```powershell
.\installer\new-test-cert.ps1        # test-cert.pfx 생성 (CurrentUser\My)
$env:DM_SIGN_PFX = "$PWD\installer\test-cert.pfx"
$env:DM_SIGN_PFX_PASSWORD = "test1234"
.\installer\build.ps1
```

서명은 붙지만 검증에서 **"인증서 체인이 신뢰되지 않음"** 경고가 납니다 — 자체 서명이라
정상입니다. **자체 서명은 SmartScreen에 전혀 도움이 되지 않으므로 배포에는 쓰지 마십시오.**

정리:
```powershell
Remove-Item .\installer\test-cert.pfx
Get-ChildItem Cert:\CurrentUser\My | ? Subject -match 'DiskMigrator Test' | Remove-Item
```

---

## 5. 배포 전 점검

1. 실제 CA(OV/EV) 인증서 또는 클라우드 HSM 준비
2. `exe` 메타데이터의 **회사/제품명**을 인증서 발급 주체와 **정확히 일치**시키기
   (`src/DiskMigrator.App/DiskMigrator.App.csproj` 의 `Company`/`Product`)
3. `build.ps1` 로 서명된 설치 프로그램 생성
4. 서명 확인: `signtool verify /pa /v installer\output\DiskMigrator-Setup-*.exe`
5. 가능하면 백신 오탐 화이트리스트(주요 벤더에 제출)도 진행
