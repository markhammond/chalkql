-- §3.5, D161: a filter over an undisclosed column compares the placeholder, which under
-- PlaceholdersAsNull is NULL, so the comparison is UNKNOWN for every row.
SELECT id FROM members WHERE postcode = '2000' ORDER BY id
