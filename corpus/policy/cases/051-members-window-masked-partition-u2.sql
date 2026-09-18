-- §3.1: a window key on a masked column partitions on the mask -- an untainted value is
-- ordinary data to every operator, windows included.
SELECT id, ROW_NUMBER() OVER (PARTITION BY first_name ORDER BY id) AS rn
FROM members ORDER BY id
