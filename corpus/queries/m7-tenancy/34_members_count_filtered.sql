-- §36.4. The ungrouped form of the same count: one group of every row the principal can see. For a
-- counter in O1 and O2 that is five rows, but the floor counts the rows the test selects (D364):
-- one member's identifier selects one, so the match count is withheld.
-- expect: principals(all)
-- expect: guarded
-- expect: tested
SELECT COUNT(*) FILTER (WHERE national_id = ?) AS n FROM members
