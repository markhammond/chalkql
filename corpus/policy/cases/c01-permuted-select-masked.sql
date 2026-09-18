-- CALCITE-1584, CALCITE-3922, CALCITE-4803, CALCITE-6070: a select list that names the table's
-- columns out of order, with a masked column in two of the four positions. PROJECT_REMOVE and
-- PROJECT_MERGE rewrite this projection and RelFieldTrimmer moves it; the report is built from
-- RelRoot.fields, so a rule that renumbered the list mislabels a mask as full.
SELECT postcode, last_name, id, first_name FROM members
