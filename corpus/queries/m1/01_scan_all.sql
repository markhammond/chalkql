-- expect: count(Read)=1
-- expect: not(Project)
-- expect: root_collation=[ts asc_nulls_last, symbol asc_nulls_last]
SELECT * FROM bars
