-- D203 (part 2b): `statistical` under AGGREGATE_ONLY rather than under MASKED. A raw predicate on
-- a population-only column, in a statement whose every output column is an aggregate, with the
-- group suppressed if it falls below the floor.
SELECT COUNT(*) AS n FROM orders WHERE amount >= 100
