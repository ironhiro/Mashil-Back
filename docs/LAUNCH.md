# 출시 전 설정 (OAuth · Sentry · App Links)

코드는 준비돼 있고, 콘솔에서 만든 값을 넣기만 하면 켜진다. 값이 없으면 그 기능은 꺼진 채로 동작한다
(`GET /api/auth/providers` 가 빈 목록, Sentry 없음, 확인 파일 404).
공개 식별자는 `--set-env-vars`, 비밀값은 `secret set` + `secretref:` 로 넣는다. 개발(`-dev`)에서 먼저 확인하고 운영에 넣는다.

마실은 앱 전용이라 로그인은 앱의 네이티브 SDK 로 한다. 웹 리디렉션(인가 코드) 로그인은 쓰지 않으므로
제공자 콘솔에 웹 Redirect URI 를 등록하지 않아도 된다.

## 0. 앱 서명 키 (먼저)

OAuth 와 App Links 모두 앱 서명 인증서를 등록하므로 이것부터 정한다.

- 업로드 키스토어를 만들고 안전한 곳에 백업 (저장소에 넣지 않는다)
- Play 콘솔에서 Play 앱 서명 사용 → **앱 서명 키**와 **업로드 키**의 SHA-1·SHA-256 확인
- 디버그 키: `keytool -list -v -keystore ~/.android/debug.keystore -storepass android -alias androiddebugkey`

| 등록할 곳 | 필요한 값 |
|---|---|
| 카카오 Android 플랫폼 | 키 해시 (SHA-1 의 base64) — 디버그·업로드·앱 서명 키 |
| Google Android 클라이언트 | SHA-1 — 디버그·업로드·앱 서명 키 (각각 클라이언트 하나) |
| 서버 `App__Links__AndroidSha256__*` | SHA-256 — 앱 서명 키·업로드 키 (dev 는 디버그 키도) |

카카오 키 해시: `keytool -exportcert -alias <alias> -keystore <keystore> | openssl sha1 -binary | openssl base64`

## 1. Kakao

docs/AUTH.md §1. 콘솔에서 카카오 로그인·OIDC ON, 동의항목은 닉네임만, Android 패키지 `dev.ironhiro.mashil` + 키 해시, iOS 번들 ID.

```bash
for app in mashil-api-dev mashil-api; do
  az containerapp update -g rg-mashil -n $app --set-env-vars \
    "OAuth__Kakao__AppId=<앱 ID>" "OAuth__Kakao__Audiences__0=<네이티브 앱 키>"
  az containerapp secret set -g rg-mashil -n $app --secrets "kakao-admin-key=<Admin 키>"
  az containerapp update -g rg-mashil -n $app --set-env-vars "OAuth__Kakao__AdminKey=secretref:kakao-admin-key"
done
```

앱 빌드: `env/{staging,prod}.json` 의 `KAKAO_NATIVE_APP_KEY` (이미 들어 있음).

## 2. Google

docs/AUTH.md §2. Google Cloud 프로젝트 → OAuth 동의 화면(외부, 범위 openid 만) → 클라이언트 ID: 웹 1, Android (SHA-1 마다), iOS 1.

```bash
for app in mashil-api-dev mashil-api; do
  az containerapp update -g rg-mashil -n $app --set-env-vars \
    "OAuth__Google__ClientIds__0=<웹 클라이언트 ID>" "OAuth__Google__ClientIds__1=<iOS 클라이언트 ID>"
done
```

앱 빌드: `GOOGLE_SERVER_CLIENT_ID`(웹 클라이언트 ID), `GOOGLE_IOS_CLIENT_ID`. 동의 화면은 "프로덕션 게시"로 바꿔야 테스트 사용자 밖에서 로그인된다.

## 3. Apple (멤버십 갱신 후)

docs/AUTH.md §3. iOS 로 다른 소셜 로그인을 내면 Sign in with Apple 은 심사 필수.

```bash
for app in mashil-api-dev mashil-api; do
  az containerapp update -g rg-mashil -n $app --set-env-vars \
    "OAuth__Apple__ClientIds__0=dev.ironhiro.mashil" "OAuth__Apple__TeamId=<Team ID>" "OAuth__Apple__KeyId=<Key ID>"
  az containerapp secret set -g rg-mashil -n $app --secrets \
    "apple-private-key=<.p8 내용>" "oauth-token-key=$(openssl rand -base64 32)"
  az containerapp update -g rg-mashil -n $app --set-env-vars \
    "OAuth__Apple__PrivateKey=secretref:apple-private-key" "OAuth__TokenEncryptionKey=secretref:oauth-token-key"
done
```

`OAuth__TokenEncryptionKey` 는 환경마다 따로 만들고 바꾸지 않는다 (저장된 Apple refresh token 을 못 읽게 된다).

확인: `curl -s https://toilets-dev.ironhiro.dev/api/auth/providers`

## 4. Sentry

sentry.io 에서 프로젝트 두 개 (예: `mashil-api` .NET, `mashil-web` React). 환경(environment)으로 dev 와 운영을 나누므로 프로젝트는 환경별로 만들지 않는다.

- 서버: 앱마다 `Sentry__Environment` 가 이미 들어 있다 (production / development)
  ```bash
  for app in mashil-api-dev mashil-api mashil-worker-dev mashil-worker; do
    az containerapp secret set -g rg-mashil -n $app --secrets "sentry-dsn=<mashil-api DSN>"
    az containerapp update -g rg-mashil -n $app --set-env-vars "Sentry__Dsn=secretref:sentry-dsn"
  done
  ```
- 웹: 백엔드 저장소 Settings → Variables → `VITE_SENTRY_DSN` (웹 DSN 은 브라우저에 공개되는 값). api 이미지를 다시 만들면 들어간다.
  environment 는 웹이 접속 호스트로 정한다 (Mashil-Front `src/lib/sentryEnv.ts`).
- Sentry 프로젝트 설정 → Security & Privacy → **Data Scrubber** 와 **IP 주소 저장 안 함** 켜기 (코드에서도 지우지만 한 번 더)

## 5. App Links

docs/APP-LINKS.md.

## 6. 예전 비밀번호 계정 정리 (출시 직전 한 번)

운영은 OAuth(카카오·Google)만 쓴다. 이메일·비밀번호 가입은 개발에서만 켜지고(`Auth:LegacyPasswordLogin`), 비밀번호는 PBKDF2 해시로만 저장한다.
운영 DB 에 비밀번호 해시가 남지 않도록 출시 직전에 정리한다. 탈퇴와 같은 방식이라 댓글·평점은 "탈퇴한 사용자"로 남는다.

```bash
# 백업 먼저. 미리보기 수를 보고, 끝에 남은 해시가 0 인지 확인
psql "$DATABASE_URL" -v ON_ERROR_STOP=1 -f deploy/sql/purge-legacy-password-accounts.sql
```

운영 설정에 `Auth__LegacyPasswordLogin` 이 없거나 `false` 인지도 확인한다 (켜져 있으면 `/api/auth/register` 가 열린다).
