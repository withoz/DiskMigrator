# DiskMigrator 브랜드 자료

로고를 직접 수정할 때 필요한 모든 값을 모았습니다. **원본은 이 폴더의 SVG 파일들입니다.**
`docs/assets/logo.png` · `logo.ico` 와 앱의 `AppIcon.ico` 는 이 SVG를 **Inkscape 렌더러로** 내보낸 것입니다(형태 왜곡 없음).

## 파일

| 파일 | 용도 |
|---|---|
| `mark.svg` | 순수 DM 마크. `currentColor` — 어디에 넣든 글자색을 따라감 |
| `mark-black.svg` | 검정 마크 (밝은 바탕용) |
| `mark-white.svg` | 흰 마크 (어두운 바탕용) |
| `app-icon.svg` | 앱 아이콘 — 다크 라운드 사각 + 흰 마크. **여기서 .ico 재생성** |
| `app-icon_F.svg` | 작업용 보관본 (참고) |
| `lockup.html` | 마크 + 워드마크 가로 배치(정식 락업). 라이트/다크 자동 |

## 마크 기하 — 절대 바뀌면 안 되는 값

마크는 **D(채운 글자, 카운터 뚫림) + M(각진 획)** 으로 이루어집니다. 둘의 대비가 정체성입니다.

- **D** = `fill`(채움), `fill-rule="evenodd"` 로 안쪽 카운터가 뚫림
- **M** = `stroke`(획) **굵기 36**, `stroke-linecap="round"`(끝 둥글게),
  `stroke-linejoin="miter"` + **`stroke-miterlimit="2.2"`** — 이 낮은 miterlimit이
  오른쪽 위 뾰족한 꼭지를 **평평하게 잘라** 줍니다. (miterlimit을 키우면 다시 뾰족해집니다.)
- 마크 viewBox: `56 95 256 165`

```
D(fill, evenodd):
  m 95.984375,107.03125 h 50.031255 c 42.9432,0 69.21758,31.95561 69.17612,61.12102
  -0.0446,31.36872 -17.96704,62.86336 -69.17612,62.86336 H 95.984375
  c -13.254834,0 -24,-10.74517 -24,-24 v -75.98438 c 0,-13.25483 10.745166,-24 24,-24 z
  m 14.000005,32.67188 h 36.03125 c 28,0 37.68751,16.98437 37.68751,29.98437
  0,13 -9.68751,28.65626 -37.68751,28.65626 h -36.03125 z

M(stroke 36, linecap round, miterlimit 2.2):
  M 180.03125,138.04688 213.39063,229.34375 284,120 280.01563,232
```

> 이 값은 Inkscape에서 확정한 형태입니다. 모양을 바꾸려면 `mark.svg`(또는 `app-icon.svg`)를
> Inkscape로 열어 편집하고, 아래 "앱 아이콘 재생성"으로 .ico를 다시 만드십시오.

## 색

| 이름 | HEX | 쓰임 |
|---|---|---|
| Ink (다크) | `#26282B` | 밝은 바탕의 마크·아이콘 배경 |
| White | `#FFFFFF` | 어두운 바탕의 마크 |
| Header Ink | `#474540` | 앱 헤더 마크(순수 검정보다 10% 부드럽게) |
| Wordmark (라이트) | `#57606A` | 밝은 바탕 글자 |
| Wordmark (다크) | `#7E838A` | 어두운 바탕 글자 |
| Wordmark (앱) | `#837B72` | 앱 헤더의 Muted 색 |

## 워드마크 (글자)

- 문구: **DISKMIGRATOR** (전부 대문자)
- 글꼴: Segoe UI (system-ui) · 굵기 400(Regular)
- 자간: `letter-spacing: 0.34em`
- 글자 크기: 마크 폭 150px 기준 **23px**

## 락업(마크+글자) 배치

- 순서: 마크 → 세로 구분선 → 워드마크
- 마크와 워드마크 간격: `1.375rem`
- 구분선(1px, 색 = 라이트 `#D9D3CA` / 다크 `#3A3D42`) 왼쪽 여백: `1.375rem`
- 세로 정렬: 가운데

## 앱 아이콘 재생성 방법 (Inkscape 렌더러 — 형태 그대로)

명령 프롬프트에서 각 크기 PNG를 내보낸 뒤 하나의 `.ico`로 묶습니다.

```
"C:\Program Files\Inkscape\bin\inkscape.com" app-icon.svg ^
    --export-type=png --export-width=256 --export-height=256 --export-filename=icon-256.png
```

16 · 32 · 48 · 64 · 128 · 256 px를 만들어 `.ico`로 합치고, `src/DiskMigrator.App/AppIcon.ico`
를 교체한 뒤 다시 빌드하면 창·작업표시줄 아이콘에 반영됩니다.
(이 저장소는 `scratchpad/build-ico.ps1` 스크립트로 이 과정을 한 번에 처리했습니다.)

> 아이콘 배경 라운드 반경(rx=76)과 마크 위치는 340 캔버스의 약 22%로 잡혀 있습니다.
> 캔버스 크기를 바꾸면 `app-icon.svg`의 `rx`·여백을 같은 비율로 다시 계산하십시오.

## 앱 헤더에 적용된 곳

`src/DiskMigrator.App/MainWindow.xaml` 헤더의 Viewbox가 같은 경로를 씁니다
(D는 `Fill`, M은 `Stroke` 36 + `StrokeMiterLimit=2.2`, 색은 `LogoInk`). 아이콘과 형태가 동일합니다.
