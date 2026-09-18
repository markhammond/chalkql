-- §36.4. The ungrouped form of the same count: one group of every row the principal can see. For a
-- counter in O1 and O2 that is five rows, which clears the floor, so the match count is disclosed.
-- expect: principals(all)
-- expect: guarded
-- expect: tested
SELECT COUNT(*) FILTER (WHERE national_id = ?) AS n FROM members
