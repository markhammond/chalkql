-- D261 class 6. A grouping key partitions by the value, which a test verdict never grants: for the
-- support desk the key is the placeholder, one group for every row it can see.
-- expect: principals(all)
-- expect: policy(u9)
SELECT national_id, COUNT(*) AS n FROM members GROUP BY national_id ORDER BY national_id
