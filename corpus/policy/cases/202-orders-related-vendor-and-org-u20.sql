-- D265 §8, D222. The same statement as a principal holding both grants: the organisation's rule is
-- written first and stops on match, so the value where the organisation reaches the row and the
-- vendor's placeholder where the path alone does.
SELECT id, org_id FROM orders ORDER BY id
