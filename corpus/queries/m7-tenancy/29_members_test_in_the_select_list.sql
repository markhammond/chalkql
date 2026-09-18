-- §36.4. The select-list form of the test verdict (D261): `national_id` is a column the support
-- desk may compare and never read, so the comparison is computed in the leaf over the raw value and
-- this column is the one boolean it discloses. Every other principal sees the placeholder compared,
-- which is NULL compared, which is NULL.
-- expect: principals(all)
-- expect: tested
-- expect: policy(u9)
SELECT id, national_id = ? AS confirmed FROM members ORDER BY id
