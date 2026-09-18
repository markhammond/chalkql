-- expect: has(IndexLookup)
-- expect: index(ix_lineitem_l_orderkey)
-- expect: not(Filter)
-- expect: ranges=1
-- expect: rows_scanned=produced
SELECT * FROM lineitem WHERE l_orderkey = ?
