-- Grouped by a field, aggregating another: the fields are projected before the aggregate, so
-- nothing groups or aggregates a composite value; a NULL venue groups as a NULL name.
-- expect: has(HashAggregate)
-- expect: has_field_access
SELECT q.venue.name AS venue, COUNT(*) AS quotes, MAX(q.bid.price) AS best_bid
FROM quotes q
GROUP BY q.venue.name
ORDER BY venue
