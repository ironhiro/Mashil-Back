UPDATE user_profiles SET "Role" = 'Admin' WHERE "Email" = 'admin@mashil.local';
SELECT "Id", "Email", "DisplayName", "Role", "CreatedAt"
FROM user_profiles
WHERE "Email" = 'admin@mashil.local';
