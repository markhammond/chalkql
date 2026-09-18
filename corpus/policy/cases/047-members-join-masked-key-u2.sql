-- §3.1: a join key on a masked column joins the masks, so two members with different names but
-- the same initial now match. Masking creates collisions, and that is the disclosed semantics.
SELECT a.id AS a_id, b.id AS b_id
FROM members a JOIN members b ON a.first_name = b.first_name AND a.id < b.id
ORDER BY a_id, b_id
