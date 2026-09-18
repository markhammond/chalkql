-- §8 corpus 10. A principal with no grant gets zero rows and no error; the report says
-- visibility = NONE, and RefuseWhenNoVisibleRows is what turns that into an exception.
-- expect: principals(all)
SELECT first_name FROM members
