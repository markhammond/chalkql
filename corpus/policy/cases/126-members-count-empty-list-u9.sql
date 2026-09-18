-- D197: an empty list makes its membership test FALSE, which is three-valued-correct for an
-- empty set; the other disjunct survives the fold and is the whole predicate.
SELECT COUNT(*) AS n FROM members
