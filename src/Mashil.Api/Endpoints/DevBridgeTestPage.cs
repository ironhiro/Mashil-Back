namespace Mashil.Api.Endpoints;

/// <summary>
/// 개발 환경 전용: Flutter 셸(P5)의 JS 브리지를 웹 없이 시험하는 페이지 (<c>/dev/bridge</c>).
/// 웹(Mashil-web)이 구현할 <c>nativeBridge.ts</c> 와 같은 원시 프로토콜만 쓴다:
/// <c>MashilBridge.postMessage(JSON {requestId, method, params})</c> → <c>window.__mashilBridgeReply(requestId, result)</c>.
/// API 와 같은 출처라 CORS 없이 <c>/api/auth/*</c> 를 부른다.
/// </summary>
public static class DevBridgeTestPage
{
    public static void MapDevBridgeTestPage(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment()) return;
        app.MapGet("/dev/bridge", () => Results.Content(Html, "text/html; charset=utf-8")).ExcludeFromDescription();
    }

    private const string Html = """
        <!doctype html>
        <html lang="ko"><head>
        <meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <title>마실 · 브리지 테스트 (dev)</title>
        <style>
          :root{--accent:#1F6F69;--bg:#fff;--fg:#1b1b1b;--muted:#666;--line:#e3e3e3}
          @media (prefers-color-scheme:dark){:root{--bg:#161818;--fg:#ececec;--muted:#9a9a9a;--line:#333}}
          body{margin:0;padding:16px;font-family:system-ui,sans-serif;background:var(--bg);color:var(--fg)}
          h1{font-size:18px;margin:0 0 4px} p.sub{margin:0 0 16px;color:var(--muted);font-size:13px}
          .grid{display:grid;grid-template-columns:1fr 1fr;gap:8px}
          button,a.btn{font:inherit;padding:12px;border-radius:10px;border:1px solid var(--line);background:transparent;color:var(--fg);text-align:center;text-decoration:none}
          button.primary{background:var(--accent);color:#fff;border-color:var(--accent)}
          pre{white-space:pre-wrap;word-break:break-all;background:rgba(127,127,127,.12);padding:12px;border-radius:10px;font-size:12px;min-height:80px}
          .state{font-size:13px;margin:12px 0}
        </style></head><body>
        <h1>브리지 테스트</h1>
        <p class="sub">개발 환경 전용 · Flutter 셸의 MashilBridge 를 원시 프로토콜로 호출</p>
        <div class="state" id="state">브리지 확인 중…</div>
        <div class="grid">
          <button onclick="run('getAppInfo')">앱 정보</button>
          <button onclick="run('getLocation')">현재 위치</button>
          <button onclick="run('openDirections',{lat:37.5665,lng:126.9780,name:'서울시청'})">길찾기 (서울시청)</button>
          <button onclick="run('openSettings')">앱 설정 열기</button>
          <button class="primary" onclick="login('kakao')">카카오 로그인</button>
          <button onclick="me()">내 정보 (API)</button>
          <button onclick="logout()">로그아웃</button>
          <button onclick="del()">탈퇴 (재인증)</button>
          <a class="btn" href="tel:0212345678">전화 링크</a>
          <a class="btn" href="https://map.kakao.com/">외부 링크</a>
        </div>
        <pre id="out"></pre>
        <script>
        const out = document.getElementById('out');
        const log = (label, v) => { out.textContent = label + '\n' + (typeof v === 'string' ? v : JSON.stringify(v, null, 2)) + '\n\n' + out.textContent; };
        const pending = {};
        window.__mashilBridgeReply = (id, result) => { const r = pending[id]; delete pending[id]; if (r) r(result); };
        const hasBridge = () => typeof window.MashilBridge !== 'undefined' && typeof window.MashilBridge.postMessage === 'function';
        let seq = 0;
        function call(method, params) {
          return new Promise((resolve, reject) => {
            if (!hasBridge()) return reject(new Error('MashilBridge 없음 (앱 밖에서 열림)'));
            const id = 'r' + Date.now() + '-' + (seq++);
            pending[id] = resolve;
            setTimeout(() => { if (pending[id]) { delete pending[id]; reject(new Error('timeout: ' + method)); } }, 120000);
            window.MashilBridge.postMessage(JSON.stringify({ requestId: id, method, params: params || {} }));
          });
        }
        async function run(method, params) {
          try { log(method, await call(method, params)); } catch (e) { log(method + ' 실패', String(e)); }
        }
        const api = async (method, path, body, token) => {
          const res = await fetch(path, { method, headers: { 'Content-Type': 'application/json', ...(token ? { Authorization: 'Bearer ' + token } : {}) }, body: body ? JSON.stringify(body) : undefined });
          const text = await res.text();
          let json; try { json = text ? JSON.parse(text) : null; } catch { json = text; }
          return { status: res.status, body: json };
        };
        const tokens = () => { try { return JSON.parse(localStorage.getItem('mashil.dev.tokens') || 'null'); } catch { return null; } };
        const mask = s => s ? s.slice(0, 6) + '…' + s.slice(-4) : s;
        async function login(provider) {
          try {
            const { body: n } = await api('GET', '/api/auth/nonce');
            const cred = await call('signIn', { provider, nonce: n.nonce });
            log('signIn (네이티브)', { ok: cred.ok, provider: cred.provider, reason: cred.reason, idToken: mask(cred.idToken), accessToken: mask(cred.accessToken) });
            if (!cred.ok) return;
            const r = await api('POST', '/api/auth/oauth/' + provider, { idToken: cred.idToken, accessToken: cred.accessToken, authorizationCode: cred.authorizationCode, nonce: n.nonce, device: (await call('getAppInfo')).platform, displayName: cred.displayName });
            if (r.status === 200) {
              localStorage.setItem('mashil.dev.tokens', JSON.stringify({ provider, access: r.body.accessToken, refresh: r.body.refreshToken }));
              log('서버 로그인 200', { userId: r.body.userId, displayName: r.body.displayName, isNewUser: r.body.isNewUser, consentRequired: r.body.consentRequired });
            } else log('서버 로그인 ' + r.status, r.body);
          } catch (e) { log('로그인 실패', String(e)); }
        }
        async function me() {
          const t = tokens(); if (!t) return log('내 정보', '로그인 안 됨');
          const r = await api('GET', '/api/auth/me', null, t.access); log('GET /api/auth/me ' + r.status, r.body);
        }
        async function logout() {
          const t = tokens(); if (!t) return log('로그아웃', '로그인 안 됨');
          await api('POST', '/api/auth/logout', { refreshToken: t.refresh });
          try { log('signOut', await call('signOut', { provider: t.provider })); } catch (e) { log('signOut 실패', String(e)); }
          localStorage.removeItem('mashil.dev.tokens');
        }
        async function del() {
          const t = tokens(); if (!t) return log('탈퇴', '로그인 안 됨');
          try {
            const { body: n } = await api('GET', '/api/auth/nonce');
            const cred = await call('signIn', { provider: t.provider, nonce: n.nonce });
            if (!cred.ok) return log('재인증 취소', cred);
            const r = await api('POST', '/api/auth/me/withdraw', { provider: t.provider, idToken: cred.idToken, accessToken: cred.accessToken, authorizationCode: cred.authorizationCode, nonce: n.nonce }, t.access);
            log('POST /api/auth/me/withdraw ' + r.status, r.body || '(본문 없음)');
            if (r.status === 204) { await call('signOut', { provider: t.provider }).catch(() => {}); localStorage.removeItem('mashil.dev.tokens'); }
          } catch (e) { log('탈퇴 실패', String(e)); }
        }
        const showState = () => {
          const t = tokens();
          document.getElementById('state').textContent = (hasBridge() ? '✅ 앱 브리지 연결됨' : '⚠️ 브리지 없음 (일반 브라우저)') + (t ? ' · 로그인: ' + t.provider : ' · 로그아웃 상태');
        };
        window.addEventListener('mashilbridgeready', showState);
        setTimeout(showState, 300);
        setInterval(showState, 3000);
        </script>
        </body></html>
        """;
}
