#!/bin/sh
set -eu
for svc in identity catalog inventory launch order payment fulfillment notification; do
  echo "migrating bloom_$svc"
  "/bundles/$svc" --connection "Host=$PG_HOST;Database=bloom_$svc;Username=$PG_USER;Password=$PG_PASSWORD"
done
