-- A4: and with an offset as well.
-- D276: the copies carry `offset + fetch` and no offset of their own — every branch must offer its
-- first six candidates, and the global heap applies the offset once to what they produce between
-- them.
-- expect: has(SetOp)
-- expect: count(TopN)=3
SELECT id, amount FROM sales
UNION ALL
SELECT id, amount FROM sales WHERE id < 4
ORDER BY amount NULLS LAST, id
OFFSET 2 ROWS FETCH NEXT 4 ROWS ONLY
