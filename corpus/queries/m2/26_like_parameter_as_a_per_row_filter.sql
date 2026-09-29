-- D314: where a scan is the cheaper plan, a parameter pattern is an ordinary per-row filter, its
-- pattern compiled once per execution when its value is known. Before, a LIKE whose pattern was a
-- parameter could only be a prefix lookup: this statement refused to prepare.
-- expect: has(Filter)
-- expect: not(IndexLookup)
SELECT symbol, ts FROM bars_small WHERE symbol LIKE ?
