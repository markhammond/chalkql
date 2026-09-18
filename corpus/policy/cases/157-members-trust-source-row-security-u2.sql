-- D156: `trust_source_row_security` skips Filter_R for that source's tables and nothing else --
-- the disclosures are still Chalk's, so rows the source returns from outside the principal's
-- scope come back with every protected column at its placeholder.
SELECT * FROM members ORDER BY id
