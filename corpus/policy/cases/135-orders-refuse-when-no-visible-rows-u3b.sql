-- D207: RefuseWhenNoVisibleRows turns `visibility = NONE` into POLICY, and nothing else. Here
-- the created-by disjunct keeps visibility at SOME, so zero rows is the honest answer.
SELECT COUNT(*) AS n FROM orders
