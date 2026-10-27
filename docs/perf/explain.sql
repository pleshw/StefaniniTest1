-- C55: EXPLAIN (ANALYZE, BUFFERS) of the Orders queries on the seeded database (see seed.sql).
-- The statements mirror what OrderQueries.ListQuery, GetAsync, GetHistoryAsync and the create path send.
\set client '''00000000-0000-0000-0000-000000000042'''
\echo === 1. List: one client, newest first, first page (ix_orders_client_created)
EXPLAIN (ANALYZE, BUFFERS) SELECT o.id, o.number, o.status, o.total, o.created_at FROM orders o
 WHERE o.client_id = :client::uuid ORDER BY o.created_at DESC, o.id LIMIT 21;
\echo === 2. List: one client, keyset page 2
EXPLAIN (ANALYZE, BUFFERS) SELECT o.id, o.number, o.status, o.total, o.created_at FROM orders o
 WHERE o.client_id = :client::uuid AND (o.created_at < now() - interval '30 days' OR (o.created_at = now() - interval '30 days' AND o.id > gen_random_uuid()))
 ORDER BY o.created_at DESC, o.id LIMIT 21;
\echo === 3. List: admin, by status (ix_orders_status_created)
EXPLAIN (ANALYZE, BUFFERS) SELECT o.id, o.number, o.status, o.total, o.created_at FROM orders o
 WHERE o.status = 'OnHold' ORDER BY o.created_at DESC, o.id LIMIT 21;
\echo === 4. List: admin, no filter
EXPLAIN (ANALYZE, BUFFERS) SELECT o.id, o.number, o.status, o.total, o.created_at FROM orders o
 ORDER BY o.created_at DESC, o.id LIMIT 21;
\echo === 5. List: admin, status and date range
EXPLAIN (ANALYZE, BUFFERS) SELECT o.id, o.number, o.status, o.total, o.created_at FROM orders o
 WHERE o.status = 'Confirmed' AND o.created_at >= now() - interval '7 days' AND o.created_at < now()
 ORDER BY o.created_at DESC, o.id LIMIT 21;
\echo === 6. Search: full text OR partial client name, as the API sends it
EXPLAIN (ANALYZE, BUFFERS) SELECT o.id, o.number, o.status, o.total, o.created_at FROM orders o
 WHERE (o.search_vector @@ websearch_to_tsquery('simple', 'carla silva') OR o.client_name ILIKE '%carla silva%' ESCAPE '\')
 ORDER BY o.created_at DESC, o.id LIMIT 21;
\echo === 7. Search: order number
EXPLAIN (ANALYZE, BUFFERS) SELECT o.id, o.number, o.status, o.total, o.created_at FROM orders o
 WHERE (o.search_vector @@ websearch_to_tsquery('simple', 'ORD-00012345') OR o.client_name ILIKE '%ORD-00012345%' ESCAPE '\')
 ORDER BY o.created_at DESC, o.id LIMIT 21;
\echo === 8. Detail: order by id
EXPLAIN (ANALYZE, BUFFERS) SELECT o.* FROM orders o WHERE o.id = (SELECT id FROM orders LIMIT 1 OFFSET 1000);
\echo === 9. Detail: items of an order
EXPLAIN (ANALYZE, BUFFERS) SELECT i.* FROM order_items i WHERE i.order_id = (SELECT id FROM orders LIMIT 1 OFFSET 1000) ORDER BY i.line_no;
\echo === 10. History of an order
EXPLAIN (ANALYZE, BUFFERS) SELECT h.* FROM order_status_history h WHERE h.order_id = (SELECT id FROM orders LIMIT 1 OFFSET 1000) ORDER BY h.seq;
\echo === 11. Create: has the client any earlier order (first-order discount)
EXPLAIN (ANALYZE, BUFFERS) SELECT EXISTS (SELECT 1 FROM orders o WHERE o.client_id = :client::uuid AND o.id <> gen_random_uuid()
 AND (o.created_at < now() OR (o.created_at = now() AND o.id < gen_random_uuid())));
\echo === 12. Create: idempotency lookup
EXPLAIN (ANALYZE, BUFFERS) SELECT * FROM idempotency_requests WHERE client_id = :client::uuid AND idempotency_key = '11111111-1111-1111-1111-111111111111'::uuid;
\echo === 13. Create: number lookup (ux_orders_number)
EXPLAIN (ANALYZE, BUFFERS) SELECT id FROM orders WHERE number = 'ORD-00012345';
\echo === 14. Create: the insert (rolled back)
BEGIN;
EXPLAIN (ANALYZE, BUFFERS) INSERT INTO orders (id, number, client_id, client_name, created_by, channel, status, item_count, currency, subtotal, discount_total, total, created_at, updated_at)
 VALUES (gen_random_uuid(), 'ORD-99999999', :client::uuid, 'Plan Test', 'perf', 'shop', 'Pending', 1, 'BRL', 10, 0, 10, now(), now());
ROLLBACK;
