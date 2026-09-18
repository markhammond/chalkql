-- CALCITE-7766, CALCITE-2626: a RIGHT join, over which JOIN_PUSH_TRANSITIVE_PREDICATES is
-- reported to produce a nullability type mismatch, and for which RelBuilder is reported not to
-- widen the nullable side. The unmatched row's NULL is a join NULL and must not be read as a
-- withheld column's placeholder.
SELECT m.id, o.id
FROM orders o RIGHT JOIN members m
  ON o.member_id = m.id AND o.org_id = m.org_id
