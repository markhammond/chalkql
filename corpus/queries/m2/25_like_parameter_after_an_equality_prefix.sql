-- F153, D314: a parameter pattern on the second key column of (ts, symbol), after an equality on the
-- first, bound to %USDT. Its literal start is empty, so the range is [(ts, ''), (ts)] — whose upper
-- side, the equality columns alone, has to be inclusive: exclusive, every key equal to them fell
-- outside it under the range contract, and an index that reads ranges by the contract (an Akade
-- tuple index, in the bars-akade configuration) answered nothing where there are five rows. The
-- lookup's residual then keeps the ones that end in USDT — all five. (A literal LIKE '%' would not
-- test this: on a NOT NULL column Calcite folds it away before any index sees it.)
-- expect: has(IndexLookup)
-- expect: not(Filter)
SELECT symbol, ts FROM bars WHERE ts = ? AND symbol LIKE ?
