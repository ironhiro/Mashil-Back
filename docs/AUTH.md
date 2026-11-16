# 인증 (P4) — 설정과 흐름

마실은 소셜 로그인만 쓴다 (Kakao, Google, Apple). 클라이언트가 IdP 에서 받은 토큰을 서버로 보내면, 서버가 IdP 공개키(JWKS)나 토큰 정보 API 로 **직접 검증**한 뒤 마실 토큰(Access 30분 + Refresh 30일, 회전)을 준다. 사용자 식별은 `(provider, provider_subject)` 만으로 하고 이메일은 쓰지 않는다.

제공자는 설정값이 들어갔을 때만 켜진다. `GET /api/auth/providers` 로 현재 켜진 제공자를 확인할 수 있다.

## 1. Kakao (먼저 진행)

### 개발자 콘솔 (기존 앱 재사용)

메뉴 이름은 콘솔 개편에 따라 조금 다를 수 있다.

| 항목 | 할 일 | 서버 설정 |
|---|---|---|
| 앱 ID | 앱 요약 정보의 숫자 ID 확인 | `OAuth:Kakao:AppId` |
| 카카오 로그인 | **활성화 ON** | — |
| OpenID Connect | **활성화 ON** (ID Token 을 받기 위해. 권장 방식) | — |
| 동의항목 | 닉네임만. 이메일은 받지 않거나 선택 (수집 최소화, NFR-07) | — |
| 플랫폼 | Android: 패키지명 + 키 해시 (디버그·릴리스 둘 다), iOS: 번들 ID, Web: 사이트 도메인 | — |
| 앱 키 | 네이티브 앱 키 (앱), JavaScript 키 (웹 SDK 를 쓸 때) — ID Token 의 `aud` 가 된다 | `OAuth:Kakao:Audiences:0`, `:1` … |
| Admin 키 | 탈퇴 시 연결 끊기(unlink)용. **서버에만** 둔다 | `OAuth:Kakao:AdminKey` |
| Redirect URI | 일반 브라우저 웹 로그인(인가 코드)을 쓸 때만 등록 | `OAuth:Kakao:RedirectUris:0` |
| Client Secret | 보안 메뉴에서 켰을 때만 | `OAuth:Kakao:ClientSecret` |

REST API 키는 지오코딩에 쓰는 `Kakao:RestApiKey` 를 그대로 쓴다 (웹 인가 코드 교환용).

### 로컬 설정 (User Secrets — API 와 Worker 가 공유)

```
dotnet user-secrets --project src/Mashil.Api set "OAuth:Kakao:AppId" "<앱 ID 숫자>"
dotnet user-secrets --project src/Mashil.Api set "OAuth:Kakao:Audiences:0" "<네이티브 앱 키>"
dotnet user-secrets --project src/Mashil.Api set "OAuth:Kakao:AdminKey" "<Admin 키>"
dotnet user-secrets --project src/Mashil.Api set "Jwt:SigningKey" "<32자 이상 랜덤>"
```

운영에서는 같은 키를 `/etc/mashil/mashil.env` 에 `OAuth__Kakao__AppId=…` 형식으로 넣는다 (`deploy/.env.example`).

### 앱에서 (Flutter, P5)

```dart
final nonce = (await api.get('/api/auth/nonce'))['nonce'];
final token = await UserApi.instance.loginWithKakaoTalk(nonce: nonce); // 카카오톡 없으면 loginWithKakaoAccount
await api.post('/api/auth/oauth/kakao', {
  'idToken': token.idToken,        // OIDC 를 켜야 온다
  'accessToken': token.accessToken, // 같이 보내면 탈퇴 때 Admin 키 없이도 연결을 끊을 수 있다
  'nonce': nonce,
  'device': 'android',
});
```

OIDC 를 켜지 못한 상황이면 `accessToken` 만 보내도 된다. 이때 서버는 `access_token_info` 의 `app_id` 가 우리 앱인지 확인한다. 이 확인이 없으면 다른 앱이 받은 카카오 토큰으로도 로그인할 수 있게 되므로, `OAuth:Kakao:AppId` 는 꼭 넣어야 한다.

## 2. Google (프로젝트 생성 후)

Google Cloud 콘솔에서 새 프로젝트 → OAuth 동의 화면 → 클라이언트 ID 를 만든다.

- **웹** 클라이언트 ID: Android 의 `serverClientId` 와 웹 로그인에 쓴다. ID Token 의 `aud` 가 이 값이 된다.
- **iOS** 클라이언트 ID: iOS 앱용.
- **Android** 클라이언트 ID: 패키지명 + SHA-1 (디버그·릴리스·Play 앱 서명 키). `aud` 로는 쓰이지 않지만 등록해야 로그인이 된다.

```
dotnet user-secrets --project src/Mashil.Api set "OAuth:Google:ClientIds:0" "<웹 클라이언트 ID>"
dotnet user-secrets --project src/Mashil.Api set "OAuth:Google:ClientIds:1" "<iOS 클라이언트 ID>"
```

앱은 `idToken` 만 보낸다. 서버는 Google 토큰을 저장하지 않으므로 탈퇴 시 서버가 끊을 것은 없다 (앱이 SDK `disconnect` 호출).

## 3. Apple (멤버십 갱신 후)

- Identifiers 의 App ID 에 **Sign in with Apple** capability 를 켠다. 웹이나 Android 에서도 쓸 거면 Services ID 를 만든다.
- Keys 에서 Sign in with Apple 키(.p8)를 만들고 Key ID 를 기록한다. 파일은 한 번만 내려받을 수 있다.

```
dotnet user-secrets --project src/Mashil.Api set "OAuth:Apple:ClientIds:0" "<번들 ID>"
dotnet user-secrets --project src/Mashil.Api set "OAuth:Apple:TeamId" "<Team ID>"
dotnet user-secrets --project src/Mashil.Api set "OAuth:Apple:KeyId" "<Key ID>"
dotnet user-secrets --project src/Mashil.Api set "OAuth:Apple:PrivateKey" "<.p8 파일 내용 전체>"
dotnet user-secrets --project src/Mashil.Api set "OAuth:TokenEncryptionKey" "<openssl rand -base64 32>"
```

앱은 `idToken` 과 `authorizationCode` 를 보낸다. 이름은 첫 로그인 때만 오므로 `displayName` 으로 같이 보낸다. 서버는 코드를 refresh token 으로 바꿔 암호화해 저장하고, 탈퇴할 때 `/auth/revoke` 로 폐기한다 (Apple 심사 요건). nonce 는 기본으로 필수이고, 원문이나 SHA-256 hex 를 요청에 넣으면 된다.

## 흐름 요약

| 단계 | API | 비고 |
|---|---|---|
| nonce | `GET /api/auth/nonce` | 5분, 1회용. DB 에 해시만 저장 (API 가 여러 대여도 동작) |
| 로그인·가입 | `POST /api/auth/oauth/{provider}` | 처음이면 자동 가입. `consentRequired` 면 동의 화면 |
| 동의 | `GET`·`POST /api/auth/consents` | 약관 버전을 올리면 다시 받는다. 동의 전 쓰기는 403 `consent_required` |
| 갱신 | `POST /api/auth/refresh` | 회전. 쓴 토큰을 다시 쓰면 그 기기 세션 전체 폐기. **동시에 하나만** |
| 로그아웃 | `POST /api/auth/logout` | 이 기기의 refresh token 폐기 |
| 탈퇴 | `POST /api/auth/me/withdraw` | 가입한 IdP 로 재인증 → 개인정보 파기 → IdP 연결 해제 (실패하면 Worker 가 재시도) |

## 탈퇴 시 남는 것과 지우는 것

| 데이터 | 처리 |
|---|---|
| 이메일, 표시 이름, 비밀번호 해시 | 즉시 null |
| 소셜 연결 (`user_identities`), refresh token, 차단 관계 | 행 삭제 |
| IdP 연결 해제 대기열 (`idp_revocations`) | 성공하면 삭제. 10번 실패하면 식별자를 지우고 Failed 로 남김 |
| 댓글, 평점, 변경 요청, 신고 | 남김. 작성자는 "탈퇴한 사용자" |
| 약관 동의 이력 | 동의 증빙으로 남김 (user_id 만 있음, LEG-03) |

개인정보처리방침의 보관 항목과 이 표를 일치시켜야 한다.

## 레거시 이메일 로그인

웹이 아직 `/api/auth/register`·`/login` 을 쓰므로 **개발 환경에서만** 켜 두었다 (`appsettings.Development.json`: `Auth:LegacyPasswordLogin = true`, `Consents:EnforceOnWrite = false`). 운영 기본값은 둘 다 꺼짐/켜짐(동의 강제)이다. 웹을 OAuth 로 옮기면 개발 설정의 두 줄과 레거시 엔드포인트, `password_hash` 컬럼을 지운다.
