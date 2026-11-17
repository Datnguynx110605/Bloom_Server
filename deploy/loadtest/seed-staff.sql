-- Seeds the load-test admin (staff, role admin, active) in bloom_identity. Idempotent.
-- Contains: Argon2id hash of LT_ADMIN_PASSWORD and the AES-GCM (SecretBox) seal of the decoded LT_ADMIN_TOTP_BASE32
-- under LT_TOTP_KEY, as Seed.StaffAsync does. Regenerate this file if LT_TOTP_KEY, LT_ADMIN_PASSWORD or
-- LT_ADMIN_TOTP_BASE32 in loadtest.env change. Dev-only throwaway values.
INSERT INTO accounts (id, type, email_normalized, email_display, password_hash, name, role, totp_secret_enc, status, version, created_at, updated_at)
VALUES ('01a1e8d9-9ab6-725f-a134-2c9ff1581aa8', 'staff', 'admin@loadtest.bloom.test', 'admin@loadtest.bloom.test', '$argon2id$v=19$m=65536,t=3,p=1$rAT1HFy8cIN5zGvj4CbLkA$C67Lzop2ep9Ih3ruiLehuECb53vAxK5JGeujszLAtbw', 'Load Test Admin', 'admin', '\x068dc4a3baab19eda19eb2ce4f0d7d948b35a3301427d07a1b51e4a7b5749f9933d35577bd909b04aeaa350fcf578dd5'::bytea, 'active', 1, now(), now())
ON CONFLICT (email_normalized) DO NOTHING;
