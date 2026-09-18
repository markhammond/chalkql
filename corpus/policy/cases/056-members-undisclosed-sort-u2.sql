-- §3.5: a sort key over an undisclosed column sorts placeholders, which are all equal.
SELECT id FROM members ORDER BY postcode, id
