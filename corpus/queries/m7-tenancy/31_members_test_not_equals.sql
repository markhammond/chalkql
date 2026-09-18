-- §36.4. `<>` is a permitted shape of its own: the support desk may ask whether an identifier is
-- *not* the one it was handed, which is the same one bit read the other way.
-- expect: principals(all)
-- expect: tested
-- expect: policy(u9)
SELECT id FROM members WHERE national_id <> ? ORDER BY id
