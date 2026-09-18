-- CALCITE-1584, CALCITE-3922, CALCITE-4803: a rename-only projection whose new names are each
-- other's old names. ProjectRemoveRule compares indexes and types and not names, so this
-- projection is "trivial" to it; PlannerPipeline.rootProject is what restores the query's
-- spelling. One column is Full and the other Masked, so a swap is visible in the report.
SELECT postcode AS first_name, first_name AS postcode FROM members
