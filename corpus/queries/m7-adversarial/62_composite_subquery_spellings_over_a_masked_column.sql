-- Class 8. The alias spellings over a sub-query that computes the composite: `(s.d).echo` reads one
-- field through the alias and `s.d.*` expands to both. Calcite merges the projections, so every
-- field is taken apart above the leaf, from the call over the leaf's sanitised column.
-- expect: principals(all)
SELECT s.id, (s.d).echo AS echo, s.d.*
FROM (SELECT id, echo_with_length(first_name) AS d FROM members) AS s
ORDER BY s.id
