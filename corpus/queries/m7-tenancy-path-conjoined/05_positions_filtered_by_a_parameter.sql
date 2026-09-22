-- A parameterised filter beside the policy's own. The parameter narrows what the statement asks for
-- and the policy narrows what may be answered; neither is allowed to widen the other.
-- expect: principals(all)
SELECT id, quantity FROM positions WHERE warehouse_code = ? ORDER BY id
