-- D265 clause (h), design 38 §8. The count over the rows a principal reaches, which for a vendor is
-- the orders some line of which carries one of its own items and no other — order 7 among them,
-- which lies in an organisation no grant of this fixture reaches, and neither order 3 nor 4 nor 6,
-- which carry no line at all: a row related to zero tenants is invisible along that axis (§4).
-- expect: principals(all)
SELECT COUNT(*) FROM orders
