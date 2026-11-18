-- 출시 전 한 번: 운영 DB 에서 예전 이메일·비밀번호 계정을 정리한다 (2026-11-18 결정).
-- 운영은 OAuth(카카오·Google) 로그인만 쓰고, 이메일 가입(/api/auth/register)은 개발에서만 켜진다 (Auth:LegacyPasswordLogin).
-- 운영 DB 에 비밀번호 해시가 남지 않도록, 비밀번호로 만든 계정을 회원 탈퇴와 같은 방식으로 지운다:
-- 이메일·표시 이름·비밀번호 해시를 비우고 Status = Deleted, 로그인 토큰·소셜 연결·차단·댓글 평가를 지운다.
-- 평점·댓글·제보·신고는 "탈퇴한 사용자"로 남는다 (탈퇴 처리와 같음, OAuthLoginService.DeleteAccountAsync).
--
-- 실행: psql "$DATABASE_URL" -v ON_ERROR_STOP=1 -f deploy/sql/purge-legacy-password-accounts.sql
-- 먼저 백업하고, 아래 미리보기 수를 확인한 뒤 실행한다. 다시 실행해도 안전하다 (대상이 없으면 아무것도 바뀌지 않음).

\echo '미리보기: 비밀번호가 있는 계정 수 (상태별)'
select "Status", count(*) from user_profiles where "PasswordHash" is not null group by 1;

begin;

create temporary table legacy_users on commit drop as
    select "Id" from user_profiles where "PasswordHash" is not null;

delete from user_identities where "UserId" in (select "Id" from legacy_users);
delete from refresh_tokens  where "UserId" in (select "Id" from legacy_users);
delete from user_blocks     where "BlockerId" in (select "Id" from legacy_users) or "BlockedId" in (select "Id" from legacy_users);
delete from comment_votes   where "UserId" in (select "Id" from legacy_users);

update user_profiles
   set "Email" = null,
       "DisplayName" = null,
       "PasswordHash" = null,
       "Status" = 'Deleted',
       "DeletedAt" = coalesce("DeletedAt", now() at time zone 'utc')
 where "Id" in (select "Id" from legacy_users);

\echo '확인: 남은 비밀번호 해시 수 (0 이어야 함)'
select count(*) as remaining_password_hashes from user_profiles where "PasswordHash" is not null;

commit;
