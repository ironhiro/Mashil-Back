# 공유 링크를 앱으로 (App Links · Universal Links)

공유한 화장실 링크 `https://toilets.ironhiro.dev/toilets/{id}` 를 누르면, 앱이 깔린 폰에서는 브라우저 대신 앱이 그 화장실을 연다.
앱이 없으면 지금처럼 웹이 열리고 웹은 앱 설치 안내를 보여 준다 (USR-04).

| 쪽 | 하는 일 | 위치 |
|---|---|---|
| 서버 | 도메인 확인 파일 두 개를 낸다. 값이 없으면 404 | `src/Mashil.Api/Endpoints/AppLinkEndpoints.cs`, 설정 `App:Links` |
| Android 앱 | `https://{웹 호스트}/toilets/*` 를 `autoVerify` 로 받는다. 호스트는 빌드 env 의 `WEB_BASE_URL` | Mashil-app `android/app/src/main/AndroidManifest.xml`, `build.gradle.kts` |
| iOS 앱 | Associated Domains `applinks:{웹 호스트}` (Apple 멤버십 갱신 후) | Mashil-app `ios/Runner/Runner.entitlements` (아직 없음) |
| 앱 공통 | 받은 주소를 웹뷰로 연다. 다른 호스트·`/` 는 무시 | Mashil-app `lib/shell/deep_links.dart` |

앱이 받는 경로는 `/toilets/*` 뿐이다. 다른 주소(로그인, 관리자 등)는 계속 웹으로 열린다.

## 서버 설정

값은 모두 공개 정보라 시크릿이 아니라 환경 변수로 넣는다.

```bash
# Android: 앱 서명 인증서 SHA-256 지문. Play 콘솔 → 앱 무결성 → 앱 서명 키 인증서 (+ 업로드 키 인증서)
az containerapp update -g rg-mashil -n mashil-api --set-env-vars \
  "App__Links__AndroidSha256__0=<Play 앱 서명 키 SHA-256>" \
  "App__Links__AndroidSha256__1=<업로드 키 SHA-256>"
# 개발 서버(staging 빌드)는 디버그 키 지문도 넣어 두면 에뮬레이터에서 시험할 수 있다
az containerapp update -g rg-mashil -n mashil-api-dev --set-env-vars \
  "App__Links__AndroidSha256__0=<디버그 키 SHA-256>"

# iOS: {Team ID}.{번들 ID}
az containerapp update -g rg-mashil -n mashil-api --set-env-vars "App__Links__AppleAppIds__0=<Team ID>.dev.ironhiro.mashil"
```

디버그 키 지문: `keytool -list -v -keystore ~/.android/debug.keystore -storepass android -alias androiddebugkey` 의 `SHA256:` 줄
(Android Studio 의 `jbr/bin/keytool`).

확인:

```bash
curl -s https://toilets.ironhiro.dev/.well-known/assetlinks.json
curl -sI https://toilets.ironhiro.dev/.well-known/apple-app-site-association   # 200, application/json, 리디렉션 없음
# Google 검증 도구
open "https://digitalassetlinks.googleapis.com/v1/statements:list?source.web.site=https://toilets.ironhiro.dev&relation=delegate_permission/common.handle_all_urls"
```

## 앱에서 시험 (Android)

```bash
adb shell pm verify-app-links --re-verify dev.ironhiro.mashil
adb shell pm get-app-links dev.ironhiro.mashil          # toilets-dev.ironhiro.dev: verified
adb shell am start -a android.intent.action.VIEW -d "https://toilets-dev.ironhiro.dev/toilets/1"
```

검증 전이어도 마지막 줄은 "열기 앱 선택" 창으로 앱을 고를 수 있다. 검증되면 바로 앱이 열린다.

## iOS (남은 일)

1. Apple Developer → Identifiers → `dev.ironhiro.mashil` → **Associated Domains** 켜기, 프로비저닝 프로필 갱신
2. Xcode → Runner → Signing & Capabilities → Associated Domains: `applinks:toilets.ironhiro.dev` (staging 은 `applinks:toilets-dev.ironhiro.dev`)
3. 서버 `App__Links__AppleAppIds__0` 설정

능력(capability)을 계정에서 켜기 전에 entitlements 만 넣으면 서명이 깨지므로, 1번 뒤에 2번을 한다.
