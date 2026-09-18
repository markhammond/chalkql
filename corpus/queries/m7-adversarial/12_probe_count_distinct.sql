-- §3.1. How many distinct values there are is a fact about the raw column an attacker would like;
-- what comes back is how many distinct *disclosed* values there are, which for an initial mask is
-- the number of distinct initials.
-- expect: principals(all)
SELECT COUNT(DISTINCT last_name) AS n FROM members
