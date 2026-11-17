#!/bin/sh
# Runs after a load/spike/stress test: waits for in-flight holds and orders to settle, then checks the invariants. Non-zero on any violation.
set -e
sleep "${VERIFY_SETTLE_SECONDS:-30}"
psql -v ON_ERROR_STOP=1 -d bloom_launch -f /verify/verify-launch.sql
psql -v ON_ERROR_STOP=1 -d bloom_order -f /verify/verify-order.sql
echo "verify: all invariants hold"
