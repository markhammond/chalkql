-- D261 §2: `IN (list)` is one shape and one lifted comparison, not a probe per element.
SELECT id FROM members WHERE national_id IN ('NID-1002', 'NID-1004') ORDER BY id
