-- §5 corpus 06. A client-bodied table function is a leaf the host produces rows for.
-- expect: has(TableFunctionScan)
-- expect: has_user_function(main.generate_series)
SELECT "value" FROM TABLE(generate_series(1, 10, 3)) ORDER BY "value"
