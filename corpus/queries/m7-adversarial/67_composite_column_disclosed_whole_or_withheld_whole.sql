-- §3.1 through a composite column, class 8 (D302). `profiles.contact` is a record member of an
-- in-process table, and its rules can only disclose it whole or withhold it whole: FULL for a
-- manager and the global grant, FULL for an agent only where the card's own `tier` field is 2 or
-- more — a condition over a field of the column it decides — and NONE, the NULL composite, for
-- everyone else. Every field of a withheld card is withheld with it, and its canaries with them.
-- The table is named with its schema in 67–70: a composite column is read only from an in-process
-- source, so over a database `profiles` stays in process beside it, where `main` is not the default.
-- expect: principals(all)
SELECT id, org_id, contact FROM main.profiles ORDER BY id
