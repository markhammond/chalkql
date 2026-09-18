-- §5 F (D168): a string range whose answer depends on the collation. Under Chalk's code-point
-- comparison 'C' and 'D' fall inside ['C', 'e') and 'a'..'f' do not, because an upper-case letter
-- sorts below a lower-case one; under a case-insensitive collation the answer is a different set.
-- The SQLite profile declares StringCollation.Binary, so the predicate pushes -- and the
-- differential at every level is what says the pushed answer is the local one (D89).
-- expect: has(RemoteQuery)
SELECT id, label, region
FROM sqlite.sales
WHERE label >= 'C' AND label < 'e' AND region > 'A'
ORDER BY id
