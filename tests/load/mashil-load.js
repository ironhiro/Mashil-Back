// 마실 API 스트레스 테스트 (k6). 로컬 개발 API 에만 돌린다 — 운영·터널 주소 금지.
//
//   k6 run tests/load/mashil-load.js                         # 기본: 20 → 400 req/s 단계 증가, 약 6분
//   k6 run -e BASE_URL=http://127.0.0.1:5099 -e PEAK=600 tests/load/mashil-load.js
//
// 시작할 때 테스트 계정 20개를 레거시 이메일 가입(개발 설정에서만 켜짐)으로 만들고 댓글을 하나씩 남긴다.
// 부하 중 쓰기(평점 5%, 도움돼요 4%, 댓글 1%)가 개발 DB 에 쌓인다.
import http from "k6/http";
import { check, fail } from "k6";
import { Trend } from "k6/metrics";

const BASE = __ENV.BASE_URL || "http://127.0.0.1:5099";
const PEAK = Number(__ENV.PEAK || 400);
const ACCOUNTS = 20;

// 서울 곳곳 (시청, 강남역, 홍대입구, 잠실, 여의도, 신촌, 종로3가, 건대입구)
const SPOTS = [
    [37.5666, 126.9784], [37.4979, 127.0276], [37.5572, 126.9245], [37.5133, 127.1002],
    [37.5219, 126.9245], [37.5551, 126.9369], [37.5704, 126.9921], [37.5404, 127.0692],
];
const RADII = [500, 1000, 2000];

const nearTrend = new Trend("near_duration", true);

export const options = {
    scenarios: {
        stress: {
            executor: "ramping-arrival-rate",
            startRate: 10,
            timeUnit: "1s",
            preAllocatedVUs: 50,
            maxVUs: 1000,
            stages: [
                { target: 20, duration: "30s" },
                { target: 20, duration: "30s" },
                { target: Math.round(PEAK / 8), duration: "30s" },
                { target: Math.round(PEAK / 8), duration: "30s" },
                { target: Math.round(PEAK / 4), duration: "30s" },
                { target: Math.round(PEAK / 4), duration: "30s" },
                { target: Math.round(PEAK / 2), duration: "30s" },
                { target: Math.round(PEAK / 2), duration: "30s" },
                { target: PEAK, duration: "30s" },
                { target: PEAK, duration: "60s" },
                { target: 0, duration: "20s" },
            ],
        },
    },
    thresholds: {
        http_req_failed: ["rate<0.01"],
        http_req_duration: ["p(95)<500"],
        "http_req_duration{name:near}": ["p(95)<500"],
        "http_req_duration{name:recommended}": ["p(95)<800"],
        "http_req_duration{kind:write}": ["p(95)<500"],
    },
    summaryTrendStats: ["avg", "med", "p(90)", "p(95)", "p(99)", "max"],
};

const json = (body) => ({ headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });
const authed = (token, extra = {}) => ({ ...extra, headers: { ...(extra.headers || {}), Authorization: `Bearer ${token}` } });
const pick = (a) => a[Math.floor(Math.random() * a.length)];
const jitter = () => (Math.random() - 0.5) * 0.02; // 약 ±1km

export function setup() {
    const run = Date.now().toString(36);
    const users = [];
    for (let i = 0; i < ACCOUNTS; i++) {
        const email = `load-${run}-${i}@test.localhost`;
        const password = `${run}-${Math.random().toString(36).slice(2)}-${i}`;
        const r = http.post(`${BASE}/api/auth/register`, json({ email, password, displayName: `부하${i}` }).body,
            { headers: { "Content-Type": "application/json" } });
        if (r.status !== 200) fail(`가입 실패 ${r.status}: 레거시 가입이 켜진 개발 API 인지 확인 (${r.body})`);
        const token = r.json("accessToken");
        const consents = http.get(`${BASE}/api/auth/consents`, authed(token)).json("documents") || [];
        if (consents.length) {
            http.post(`${BASE}/api/auth/consents`, JSON.stringify({ items: consents.map((d) => ({ docType: d.docType, version: d.version })) }),
                authed(token, { headers: { "Content-Type": "application/json" } }));
        }
        users.push({ token, id: r.json("userId") });
    }

    // 화장실 id 모음 (각 지점 근처 2km)
    const toiletIds = [];
    for (const [lat, lng] of SPOTS) {
        const rows = http.get(`${BASE}/api/toilets/near?lat=${lat}&lng=${lng}&radiusM=2000&limit=200`).json();
        rows.forEach((t) => toiletIds.push(t.id));
    }
    if (toiletIds.length === 0) fail("근처 화장실이 없음: DB 에 데이터가 있는지 확인");

    // 도움돼요 대상 댓글: 계정마다 하나
    const comments = [];
    users.forEach((u, i) => {
        const r = http.post(`${BASE}/api/toilets/${pick(toiletIds)}/comments`, JSON.stringify({ text: `부하 테스트 댓글 ${i}` }),
            authed(u.token, { headers: { "Content-Type": "application/json" } }));
        if (r.status === 201) comments.push({ id: r.json("id"), author: u.id });
    });
    return { users, toiletIds, comments };
}

export default function (data) {
    const roll = Math.random() * 100;
    const [lat0, lng0] = pick(SPOTS);
    const lat = lat0 + jitter(), lng = lng0 + jitter();
    const user = pick(data.users);
    const writeParams = (name) => authed(user.token, { headers: { "Content-Type": "application/json" }, tags: { name, kind: "write" } });

    if (roll < 50) {
        const r = http.get(`${BASE}/api/toilets/near?lat=${lat}&lng=${lng}&radiusM=${pick(RADII)}`, { tags: { name: "near", kind: "read" } });
        check(r, { "near 200": (x) => x.status === 200 });
        nearTrend.add(r.timings.duration);
    } else if (roll < 60) {
        const r = http.get(`${BASE}/api/toilets/recommended?lat=${lat}&lng=${lng}&radiusM=1000&limit=5`, { tags: { name: "recommended", kind: "read" } });
        check(r, { "recommended 200": (x) => x.status === 200 });
    } else if (roll < 75) {
        const r = http.get(`${BASE}/api/toilets/${pick(data.toiletIds)}`, { tags: { name: "detail", kind: "read" } });
        check(r, { "detail 200": (x) => x.status === 200 });
    } else if (roll < 90) {
        const r = http.get(`${BASE}/api/toilets/${pick(data.toiletIds)}/comments`, authed(user.token, { tags: { name: "comments", kind: "read" } }));
        check(r, { "comments 200": (x) => x.status === 200 });
    } else if (roll < 95) {
        const r = http.post(`${BASE}/api/toilets/${pick(data.toiletIds)}/ratings`, JSON.stringify({ score: 1 + Math.floor(Math.random() * 5) }), writeParams("rating"));
        check(r, { "rating 204": (x) => x.status === 204 });
    } else if (roll < 99) {
        const others = data.comments.filter((c) => c.author !== user.id);
        const r = http.put(`${BASE}/api/comments/${pick(others).id}/vote`, JSON.stringify({ value: pick([1, -1, 0]) }), writeParams("vote"));
        check(r, { "vote 200": (x) => x.status === 200 });
    } else {
        const r = http.post(`${BASE}/api/toilets/${pick(data.toiletIds)}/comments`, JSON.stringify({ text: "부하 테스트 댓글" }), writeParams("comment"));
        check(r, { "comment 201": (x) => x.status === 201 });
    }
}
