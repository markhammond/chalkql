-- D205: a global grant is refused at prepare unless the package option AllowGlobalGrants is on,
-- which is off by default in an interactive tier. The check belongs to the tenancy package.
SELECT * FROM members
