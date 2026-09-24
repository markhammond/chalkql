-- Nor equality: comparing two composite columns is refused by name when the statement is validated.
-- expect: error=UNSUPPORTED
SELECT q.id FROM quotes q WHERE q.bid = q.ask
