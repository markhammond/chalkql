-- D217: the second knob. `RedactedColumns` is about what a STAR surfaces and leaves a named column
-- as §3.11's placeholder (case 101); `NamedRedactedColumns.Refuse` is the host that would rather be
-- told about the column it named.
SELECT postcode FROM members ORDER BY id
