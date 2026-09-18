-- §3.1. The extrema by aggregate rather than by sort. `MIN` and `MAX` over a masked column are
-- ordinary aggregates over ordinary data — the mask is a sanitiser, and what it produces is not
-- tainted — so they are permitted and they answer about the masks.
-- expect: principals(all)
SELECT MIN(last_name) AS lo, MAX(last_name) AS hi FROM members
