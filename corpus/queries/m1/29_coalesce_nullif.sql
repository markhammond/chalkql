-- expect: has(Project)
-- expect: has_if_then
-- expect: has_function(IS_NOT_NULL)
-- expect: has_function(EQ)
-- Calcite's validator rewrites COALESCE and NULLIF into CASE before the planner ever sees them
-- (ADR 0001), so the IR carries IfThen rather than FUNCTION_ID_COALESCE / _NULLIF. Both function
-- ids stay in the IR and both kernels are implemented and unit-tested, because a third-party
-- planner may emit them; this reference planner simply never does.
SELECT symbol, COALESCE(trade_count, 0) AS tc, NULLIF(volume, 0) AS v FROM bars
