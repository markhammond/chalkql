-- expect: has(VirtualTable)
-- expect: not(Read)
SELECT * FROM bars WHERE 1 = 0
