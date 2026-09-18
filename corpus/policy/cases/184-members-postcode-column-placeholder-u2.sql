-- D162: a per-column placeholder on a STRING column wins over PlaceholdersAsNull, so a consumer
-- that must not see a NULL gets the stand-in the host chose rather than the policy's.
SELECT postcode FROM members ORDER BY id
