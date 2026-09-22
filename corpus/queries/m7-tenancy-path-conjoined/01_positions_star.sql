-- The star over the target, as every principal. `positions` holds its warehouse on its own row and
-- its supplier at the end of a path, so a grant confined along both is decided over the two rows
-- together: the reviewer for A confined to Singapore sees A's Singapore stock and neither A's Tokyo
-- stock nor B's Singapore stock.
-- expect: principals(all)
SELECT * FROM positions ORDER BY id
