-- Seed for the C55 query review: about one year of orders at the sizing rate (50,000 a month, 3 lines each),
-- 5,000 clients. Run on an empty, migrated Orders database. Not part of the product; only for EXPLAIN runs.
INSERT INTO orders (id, number, client_id, client_name, created_by, channel, status, item_count, currency, subtotal, discount_total, total, created_at, updated_at)
SELECT gen_random_uuid(),
       'ORD-' || lpad(n::text, 8, '0'),
       ('00000000-0000-0000-0000-' || lpad((n % 5000)::text, 12, '0'))::uuid,
       (ARRAY['Ana','Bruno','Carla','Diego','Elisa','Fabio','Gabriela','Hugo'])[1 + n % 8] || ' '
         || (ARRAY['Silva','Souza','Lima','Costa','Alves','Rocha','Dias','Melo'])[1 + (n / 8) % 8] || ' ' || (n % 5000),
       'seed', CASE WHEN n % 5 = 0 THEN 'admin' ELSE 'shop' END,
       (ARRAY['Confirmed','Confirmed','Confirmed','OnHold','Pending','Cancelled'])[1 + n % 6],
       3, 'BRL', 300, 0, 300,
       now() - (n * interval '52 seconds'), now()
FROM generate_series(1, 600000) AS n;
INSERT INTO order_items (order_id, line_no, sku, product_name, unit_price, catalog_version, quantity, line_discount, line_total)
SELECT o.id, l, 'SKU-' || (1 + (abs(hashtext(o.id::text)) + l) % 500), 'Product', 100, 1, 1, 0, 100
FROM orders o CROSS JOIN generate_series(1, 3) AS l;
INSERT INTO order_status_history (order_id, seq, from_status, to_status, actor, changed_at)
SELECT id, 1, NULL, 'Pending', 'seed', created_at FROM orders;
ANALYZE;
