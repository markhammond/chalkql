-- §5's negative: a SQL-bodied table function is a Calcite macro, and a macro binds its arguments
-- while the statement is validated. A dynamic parameter has no value then, and saying so is more
-- useful than Calcite's own "illegal use of dynamic parameter".
-- expect: error=UNSUPPORTED
SELECT * FROM TABLE(bars_for(?))
