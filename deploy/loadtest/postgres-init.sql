-- Runs once per `up` as the postgres superuser (db-init one-shot). Idempotent: safe to rerun.
SELECT format('CREATE DATABASE %I', d)
FROM unnest(ARRAY['bloom_identity','bloom_catalog','bloom_inventory','bloom_launch','bloom_order','bloom_payment','bloom_fulfillment','bloom_notification']) AS d
WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = d)
\gexec

-- Inventory's runtime role (migrations run as the owner, the app does not); the migration grants it its rights.
SELECT 'CREATE ROLE inventory_app LOGIN PASSWORD ''inventory-app-loadtest'''
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'inventory_app')
\gexec
