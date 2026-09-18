-- CALCITE-7663: a full-width pushed scan is rendered `SELECT *` by SqlImplementor, and Chalk
-- reads the source's answer by position. `SELECT *` is therefore a promise that the source's
-- column order equals the registered catalog's, and nothing in the plan states it.
SELECT * FROM members
