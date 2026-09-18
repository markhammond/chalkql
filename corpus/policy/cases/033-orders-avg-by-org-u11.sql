-- §8 corpus 13: the org with two orders is below the floor of 5, so its AVG is NULL while the
-- org with eight keeps its value.
SELECT org_id, AVG(amount) AS a FROM orders GROUP BY org_id ORDER BY org_id
