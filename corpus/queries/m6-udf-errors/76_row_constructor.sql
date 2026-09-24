-- A composite comes from a function (D291). The ROW constructor builds one out of columns, which the IR
-- has no node for, so it is refused naming the alternative: select the values as columns.
-- expect: error=UNSUPPORTED
SELECT symbol, ROW(symbol, "close") AS r FROM bars_small
