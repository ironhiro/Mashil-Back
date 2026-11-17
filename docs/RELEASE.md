# 버전과 배포

2026-11-17 결정: 운영은 **태그를 push 하면 자동**으로 나가고, 서버·웹 버전은 **날짜(CalVer)**로 붙인다.

## 버전

| 대상 | 형식 | 예 | 어디서 보나 |
|---|---|---|---|
| 서버 + 웹 (한 이미지) | `vYYYY.MM.DD`, 같은 날 두 번째부터 `.N` | `v2026.11.17`, `v2026.11.17.2` | `GET /healthz/version` → `release`, `server`(커밋) / `GET /version.json` → `web`(Mashil-Front 커밋) |
| 개발 서버 | `main-<sha7>` | `main-3f2a91c` | 같음 |
| 앱 | SemVer `x.y.z+빌드` | `1.2.0+57` | 스토어. 서버의 `GET /api/app/version` 이 최소·최신 버전을 알려 준다 |

- 서버와 웹은 사용자가 고르는 버전이 아니라 "언제 나갔나"만 알면 된다 → 날짜.
- 앱은 웹뷰 껍데기라 화면 변경은 웹 배포로 끝난다. 앱을 새로 내는 건 브리지·네이티브 기능이 바뀔 때.
  - 오래된 앱을 막아야 하면 `App__Versions__Android__Minimum` / `Ios__Minimum` 을 올린다 (강제 업데이트).
  - 웹은 UA 의 `MashilApp/<버전>` 으로 새 브리지 기능을 켜고 끈다 (낮은 앱에서는 숨김).

## 흐름

```
PR 병합 (Back·Front 어느 쪽이든)
  → main CI: 시험 → 이미지(api 에 웹 포함, 태그 <sha>-<run>) → 개발 배포 → 점검
  → 개발 서버에서 확인
  → tools/release.sh   (개발에 떠 있는 커밋에 v2026.11.17 태그 push)
  → release 작업: 개발 이미지에 버전 태그를 붙여 그대로 운영 배포 → 점검 → GitHub Release 노트
```

- **운영은 다시 빌드하지 않는다.** 개발에서 확인한 이미지(다이제스트)를 그대로 쓴다. 웹도 개발에 들어간 그 커밋.
- 태그 커밋과 개발 서버의 커밋이 다르면 release 가 멈춘다 (확인하지 않은 것이 운영에 가지 않게).
- 점검(`deploy/azure/ci-deploy.sh`): `/healthz/ready`, `/healthz/version` 의 release·커밋이 새것인지(새 리비전이 실제로 떴는지), `/api/app/status`, `/version.json`.
  실패하면 API·Worker 를 직전 이미지로 자동으로 되돌리고 작업은 실패로 끝난다.
- 마이그레이션은 되돌리지 않는다. 그래서 expand/contract 규칙(OPS-03)을 지킨다: 새 스키마는 직전 서버와도 돌아야 한다.
  컬럼을 지우는 건 그 컬럼을 안 쓰는 버전이 운영에 나간 **다음** 배포에서.

## 하는 법

```bash
tools/release.sh --dry   # 붙일 태그, 직전 운영 이후 병합된 PR 목록
tools/release.sh         # 태그 push → 운영 배포
gh run list -L 1         # 진행
curl -s https://toilets.ironhiro.dev/healthz/version
```

승인 단계를 두고 싶으면 GitHub → Settings → Environments → `production` 에 Required reviewers 를 켜면 된다 (코드 변경 없음).

## 되돌리기

직전 버전 태그로 다시 올린다 (이미지는 GHCR 에 버전 태그로 남아 있다):

```bash
V=v2026.11.16
for a in api worker; do
  az containerapp update -g rg-mashil -n mashil-$a --image ghcr.io/ironhiro/mashil-$a:$V --set-env-vars MASHIL_RELEASE=$V
done
```

## 앱 (출시 때 붙일 것)

- `app-v1.2.0` 태그 → CI 가 빌드번호를 실행 번호로 매겨 Play 내부 테스트 트랙과 TestFlight 에 올린다. 심사 제출은 사람이.
- 서명 키·스토어 API 키가 있어야 해서 출시 준비 때 만든다.
