# 마실 (Mashil) 브랜딩

> **잠깐, 다녀와.**
> 가까운 공중·개방 화장실을 빠르게 찾고 길안내까지 받는 한국 데이터 기반 사이드 프로젝트.

## 이름

- **마실** — 순우리말. "잠깐 동네 한 바퀴 다녀오는 짧은 외출/방문." 서비스 컨셉(잠깐 들렀다 가기)과 의미가 일치.
- 로마자: **Mashil** — 코드/도메인/패키지 네임에 그대로 사용.
- 태그라인: **잠깐, 다녀와.** — 인사말 톤. 친근하고 짧음.

## 로고

`assets/logo.svg` (마스터). `D:\Code\Mashil-web\public\logo.svg` 에 동일 사본 (프런트는 백엔드 리포 외부 — README 참고).

- 64×64 라운드 사각 프레임 (`#2D6A4F` forest sage)
- 흰색 ㅁ(미음) 도어 모티프 — "잠깐 드나드는 문" 비유
- 우상단 작은 amber 원 (`#FFB703`) — "들렀다 가는 점", 지도 핀과 톤 통일
- 단색·역상 변형은 필요 시 추후 확장 (현재 단일 자산만 유지)

## 팔레트

| 토큰 | hex | 용도 |
|---|---|---|
| `primary.main` | `#2D6A4F` | 앱바, 주요 버튼, 브랜드 컬러 |
| `primary.light` | `#52B788` | hover, 강조 배경 |
| `primary.dark` | `#1B4332` | pressed, 어두운 배경 위 텍스트 |
| `secondary.main` | `#FFB703` | 지도 핀, CTA 강조 |
| `secondary.light` | `#FFD166` | 배지, 라이트 강조 |
| `secondary.dark` | `#FB8500` | 핀 hover/pressed |
| `background.default` | `#F8F9FA` | 페이지 배경 |
| `background.paper` | `#FFFFFF` | 카드/시트 |
| `text.primary` | `#1B2021` | 본문 |
| `text.secondary` | `#525B66` | 보조 텍스트 |
| `divider` | `#E5E7EB` | 구분선 |

MUI 테마는 `D:\Code\Mashil-web\src\theme.ts` 가 단일 진실 — 새 색을 만들기 전에 기존 토큰 재사용 우선.

## 타이포그래피

- 본문: **Pretendard** (한국어 가독성 우수, 무료/오픈 라이선스). fallback: `-apple-system, BlinkMacSystemFont, Segoe UI, Roboto, sans-serif`.
- Heading: 700 weight; Body: 400/500
- 버튼: `textTransform: "none"`, weight 600 — 한글 + 영문 혼용 시 대문자 변환은 톤이 어긋남

## 톤 & 보이스

- **친근한 평어/인사체** ("잠깐, 다녀와", "여기 좋았어요")
- 응급 상황을 다루므로 **빠른 정보 제공**이 카피보다 우선 — 카피는 짧게
- 데이터 부족(좌표 없음 / 주소 없음) 같은 경계 상태는 **솔직하게** 안내 ("위치 미확정 — 검색만 가능")
