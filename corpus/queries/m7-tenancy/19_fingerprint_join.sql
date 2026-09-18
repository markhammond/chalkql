-- §8 corpus 19. An equality join on tokens: the auditor's fingerprint mask is keyed and stable, so
-- masked rows join across tenancies without either name being disclosed.
-- expect: principals(all)
SELECT m.id AS a, m2.id AS b
FROM members m JOIN members m2 ON m.first_name = m2.first_name
ORDER BY m.id, m2.id
