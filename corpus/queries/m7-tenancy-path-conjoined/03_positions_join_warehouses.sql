-- And joined to the table the directly held kind lives on, which a warehouse-confined grant reaches
-- only for its own warehouse.
-- expect: principals(all)
SELECT p.id, w.city FROM positions p JOIN warehouses w ON w.code = p.warehouse_code ORDER BY p.id
