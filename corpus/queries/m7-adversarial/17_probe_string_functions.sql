-- §3.1. Functions over the value rather than predicates on it: taking it apart, measuring it,
-- changing its case. Each is an ordinary scalar over the sanitiser's output — the length of an
-- initial is one — because being a sanitiser rather than being pure is what untaints a value.
-- `POSITION` is not here: it is outside the M1 kernel set over a string of any nullability
-- (`02-ir.md` §6), so it says nothing about this layer; the character oracle is statement 06.
-- Registered: F58 — over a redacted column `CHAR_LENGTH` answers 0 where the oracle answers NULL.
-- expect: principals(all)
SELECT id,
       SUBSTRING(first_name, 1, 3) AS part,
       CHAR_LENGTH(first_name) AS len,
       UPPER(last_name) AS upper_last
FROM members ORDER BY id
