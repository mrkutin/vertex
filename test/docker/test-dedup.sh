#!/usr/bin/env bash
# Verifies that the exit groups broker URLs by hostname and opens ONE MQTT
# session per broker (not one per URL). Boots a single Mosquitto with two
# listeners (mqtt:1883 + ws:9001) and points an exit at both endpoints of the
# same broker.
#
# Pre-fix expectation (would FAIL after this patch but verifies the bug):
#   - 2 unique client IDs connected to mosquitto-1: vtx-exit-mtl-0 + vtx-exit-mtl-1
#   - 2 discovery/exits/mtl PUBLISH events per 30s tick
#
# Post-fix expectation (PASS):
#   - 1 unique client ID connected: vtx-exit-mtl-0
#   - 1 discovery/exits/mtl PUBLISH event per 30s tick
set -euo pipefail

cd "$(dirname "$0")/../.."

cleanup() {
  docker compose --profile dedup-test down -v --remove-orphans >/dev/null 2>&1 || true
}
trap cleanup EXIT

echo "==> Building and starting exit-dedup profile"
docker compose --profile dedup-test up -d --build mosquitto-1 exit-dedup >/dev/null

echo "==> Waiting 35s for connection + first heartbeat tick"
sleep 35

echo "==> Mosquitto connect log lines mentioning vtx-exit-mtl:"
docker compose logs mosquitto-1 2>&1 | grep -E "vtx-exit-mtl" || true

echo
echo "==> Unique client IDs connected with prefix vtx-exit-mtl-"
unique_clients=$(docker compose logs mosquitto-1 2>&1 \
  | grep -Eo "vtx-exit-mtl-[0-9]+" \
  | sort -u)
echo "${unique_clients}"
client_count=$(echo "${unique_clients}" | wc -l | tr -d ' ')
echo "Count: ${client_count}"

echo
echo "==> Counting discovery/exits/mtl publishes in next 35s (subscribing fresh)"
publish_count=$(docker compose exec -T mosquitto-1 sh -c \
  "timeout 35 mosquitto_sub -h localhost -p 1883 \
    -u vtx-exit-mtl -P test123 \
    -t 'discovery/exits/mtl' -v 2>/dev/null | wc -l" || true)
publish_count=$(echo "${publish_count}" | tr -d ' \r\n')
echo "Discovery publishes observed in 35s window: ${publish_count}"

echo
echo "==> Result"
fail=0
if [ "${client_count}" != "1" ]; then
  echo "FAIL: expected exactly 1 unique client ID, got ${client_count}"
  fail=1
else
  echo "PASS: 1 unique MQTT client session (group-by-host)"
fi

# Subscribing fresh receives 1 retained + ~1 fresh publish in 35s window = 2.
# Pre-fix would yield 1 retained + ~2 fresh = 3.
if [ "${publish_count}" -gt "2" ]; then
  echo "FAIL: expected ≤2 discovery publishes in 35s, got ${publish_count}"
  fail=1
else
  echo "PASS: ≤2 discovery publishes in 35s (no duplication)"
fi

exit ${fail}
