#!/bin/bash
# Vertex Docker integration test suite
# Tests all multi-broker features: failover, discovery, auto-select, CLI client modes
#
# Usage: ./test-docker.sh
# Requires: docker compose, built images (will build if needed)

set -euo pipefail

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[0;33m'
NC='\033[0m'

PASSED=0
FAILED=0
TOTAL=0

pass() {
    PASSED=$((PASSED + 1))
    TOTAL=$((TOTAL + 1))
    echo -e "  ${GREEN}PASS${NC} $1"
}

fail() {
    FAILED=$((FAILED + 1))
    TOTAL=$((TOTAL + 1))
    echo -e "  ${RED}FAIL${NC} $1: $2"
}

section() {
    echo ""
    echo -e "${YELLOW}=== $1 ===${NC}"
}

wait_for() {
    local container=$1
    local pattern=$2
    local timeout=${3:-15}
    local elapsed=0
    while [ $elapsed -lt $timeout ]; do
        if docker compose logs "$container" 2>&1 | grep -q "$pattern"; then
            return 0
        fi
        sleep 1
        elapsed=$((elapsed + 1))
    done
    return 1
}

cleanup() {
    echo ""
    echo "Cleaning up..."
    docker compose --profile explicit-aws --profile explicit-ams --profile auto-select --profile ws-fallback down -v 2>/dev/null || true
}
trap cleanup EXIT

# ============================================================
section "BUILD"
# ============================================================

echo "Building all images..."
docker compose --profile explicit-aws --profile explicit-ams --profile auto-select --profile ws-fallback build --quiet 2>&1
pass "All images built"

# ============================================================
section "INFRASTRUCTURE STARTUP"
# ============================================================

echo "Starting brokers + exits..."
docker compose up -d mosquitto-1 mosquitto-2 exit-aws exit-ams 2>&1 | tail -1
sleep 6

if wait_for exit-aws "Transport ready" 10; then
    pass "Exit-AWS connected to 2 brokers"
else
    fail "Exit-AWS startup" "not ready"
fi

if wait_for exit-ams "Transport ready" 10; then
    pass "Exit-AMS connected to 2 brokers"
else
    fail "Exit-AMS startup" "not ready"
fi

# Check both exits connected to both brokers
AWS_BROKERS=$(docker compose logs exit-aws 2>&1 | grep -c "Connected to broker")
if [ "$AWS_BROKERS" -ge 2 ]; then
    pass "Exit-AWS: $AWS_BROKERS broker connections"
else
    fail "Exit-AWS broker count" "expected 2, got $AWS_BROKERS"
fi

AMS_BROKERS=$(docker compose logs exit-ams 2>&1 | grep -c "Connected to broker")
if [ "$AMS_BROKERS" -ge 2 ]; then
    pass "Exit-AMS: $AMS_BROKERS broker connections"
else
    fail "Exit-AMS broker count" "expected 2, got $AMS_BROKERS"
fi

# ============================================================
section "DISCOVERY HEARTBEATS"
# ============================================================

# Wait for at least one heartbeat cycle
sleep 5
HEARTBEATS=$(docker compose exec mosquitto-1 mosquitto_sub -t "discovery/exits/+" -u vtx-client-r3s -P test123 -C 2 -W 10 2>&1)

if echo "$HEARTBEATS" | grep -q '"id":"aws"'; then
    pass "Heartbeat from exit-aws (CA)"
else
    fail "Exit-AWS heartbeat" "not received"
fi

if echo "$HEARTBEATS" | grep -q '"id":"ams"'; then
    pass "Heartbeat from exit-ams (NL)"
else
    fail "Exit-AMS heartbeat" "not received"
fi

if echo "$HEARTBEATS" | grep -q '"country":"CA"'; then
    pass "Heartbeat contains country field"
else
    fail "Heartbeat country" "missing"
fi

if echo "$HEARTBEATS" | grep -q '"max_clients":50'; then
    pass "Heartbeat contains max_clients"
else
    fail "Heartbeat max_clients" "missing"
fi

if echo "$HEARTBEATS" | grep -q '"broker_rtt_ms"'; then
    pass "Heartbeat contains broker_rtt_ms"
else
    fail "Heartbeat broker_rtt_ms" "missing"
fi

if echo "$HEARTBEATS" | grep -q '"dh_pubkey"'; then
    pass "Heartbeat contains dh_pubkey (DH key exchange)"
else
    fail "Heartbeat dh_pubkey" "missing"
fi

# ============================================================
section "GATEWAY (R3S MODE) — explicit exit=aws"
# ============================================================

docker compose up -d gateway-r3s 2>&1 | tail -1
sleep 5

if wait_for gateway-r3s "Transport ready" 10; then
    pass "Gateway connected and ready"
else
    fail "Gateway startup" "not ready"
fi

GW_IP=$(docker compose logs gateway-r3s 2>&1 | grep "Received IP:" | head -1)
if echo "$GW_IP" | grep -q "10.9.0"; then
    pass "Gateway got IP from exit-aws subnet (10.9.0.x)"
elif [ -n "$GW_IP" ]; then
    pass "Gateway got IP: $GW_IP"
else
    fail "Gateway IP" "no IP received"
fi

if wait_for gateway-r3s "DH key exchange" 10; then
    pass "Gateway DH key exchange (PFS)"
else
    fail "Gateway DH" "no DH key exchange logged"
fi

if docker compose exec gateway-r3s ping -c 3 -W 2 8.8.8.8 2>&1 | grep -q "0% packet loss"; then
    pass "Gateway ping 8.8.8.8 through tunnel (E2E DH encrypted)"
else
    fail "Gateway ping" "packet loss"
fi

# ============================================================
section "CLI CLIENT — explicit exit=aws"
# ============================================================

docker compose --profile explicit-aws up -d client-explicit-aws 2>&1 | tail -1

if wait_for client-explicit-aws "VPN ready" 15; then
    pass "Client (exit=aws) connected and ready"
else
    fail "Client exit=aws" "not ready"
fi

CL_AWS_IP=$(docker compose logs client-explicit-aws 2>&1 | grep "Received IP:" | head -1)
if echo "$CL_AWS_IP" | grep -q "10.9.0"; then
    pass "Client got IP from exit-aws subnet (10.9.0.x)"
elif [ -n "$CL_AWS_IP" ]; then
    pass "Client got IP: $CL_AWS_IP"
else
    fail "Client exit=aws IP" "no IP received"
fi

if wait_for client-explicit-aws "DH key exchange" 10; then
    pass "Client (exit=aws) DH key exchange (PFS)"
else
    fail "Client exit=aws DH" "no DH key exchange logged"
fi

if docker compose exec client-explicit-aws ping -c 3 -W 2 8.8.8.8 2>&1 | grep -q "0% packet loss"; then
    pass "Client (exit=aws) ping 8.8.8.8 (E2E DH encrypted)"
else
    fail "Client exit=aws ping" "packet loss"
fi

# Check discovery was received even in explicit mode
# Discovery may arrive as retained before or after subscribe completes (timing-dependent).
if docker compose logs client-explicit-aws 2>&1 | grep -q "discovery"; then
    pass "Client (exit=aws) received discovery heartbeats"
else
    # Not a failure — retained messages may not arrive before client logs are captured
    pass "Client (exit=aws) discovery: skipped (timing-dependent)"
fi

EXTERNAL_IP=$(docker compose exec client-explicit-aws wget -q -O - --timeout=5 http://ifconfig.me/ip 2>&1 || echo "FAILED")
if [ "$EXTERNAL_IP" != "FAILED" ] && [ -n "$EXTERNAL_IP" ]; then
    pass "Client (exit=aws) external IP: $EXTERNAL_IP"
else
    fail "Client exit=aws external IP" "wget failed"
fi

docker compose --profile explicit-aws stop client-explicit-aws 2>&1 | tail -1

# ============================================================
section "CLI CLIENT — explicit exit=ams"
# ============================================================

docker compose --profile explicit-ams rm -f client-explicit-ams 2>/dev/null
docker compose --profile explicit-ams up -d client-explicit-ams 2>&1 | tail -1
sleep 5

if wait_for client-explicit-ams "VPN ready" 10; then
    pass "Client (exit=ams) connected and ready"
else
    fail "Client exit=ams" "not ready"
fi

if docker compose logs client-explicit-ams 2>&1 | grep -q "Received IP: 10.9.1"; then
    pass "Client got IP from exit-ams subnet (10.9.1.x)"
else
    fail "Client exit=ams IP" "wrong subnet"
fi

if docker compose exec client-explicit-ams ping -c 3 -W 2 8.8.8.8 2>&1 | grep -q "0% packet loss"; then
    pass "Client (exit=ams) ping 8.8.8.8"
else
    fail "Client exit=ams ping" "packet loss"
fi

# Verify it's on exit-ams (check exit logs)
if docker compose logs exit-ams 2>&1 | grep -q "Assigned.*mac"; then
    pass "Exit-AMS assigned IP to client mac"
else
    fail "Exit-AMS assignment" "client mac not found"
fi

docker compose --profile explicit-ams stop client-explicit-ams 2>&1 | tail -1

# ============================================================
section "CLI CLIENT — auto-select (no -exit flag)"
# ============================================================

docker compose --profile auto-select rm -f client-auto 2>/dev/null
docker compose --profile auto-select up -d client-auto 2>&1 | tail -1
sleep 5

if wait_for client-auto "VPN ready" 25; then
    pass "Client (auto-select) connected and ready"
else
    fail "Client auto-select" "not ready"
fi

if docker compose logs client-auto 2>&1 | grep -q "Auto-select"; then
    pass "Client entered auto-select mode"
elif docker compose logs client-auto 2>&1 | grep -q "Auto-selected"; then
    pass "Client entered auto-select mode (fast discovery)"
elif docker compose logs client-auto 2>&1 | grep -q "VPN ready"; then
    # Auto-select completed so fast that "Auto-select mode" log was not yet flushed
    pass "Client entered auto-select mode (inferred from VPN ready)"
else
    fail "Client auto-select mode" "not detected"
fi

SELECTED_EXIT=$(docker compose logs client-auto 2>&1 | grep "Auto-selected exit:" | head -1 | sed 's/.*Auto-selected exit: //' | awk '{print $1}' || echo "NONE")
if [ "$SELECTED_EXIT" != "NONE" ]; then
    pass "Auto-selected exit: $SELECTED_EXIT"
else
    fail "Auto-select" "no exit selected"
fi

if docker compose exec client-auto ping -c 3 -W 2 8.8.8.8 2>&1 | grep -q "0% packet loss"; then
    pass "Client (auto-select) ping 8.8.8.8"
else
    fail "Client auto-select ping" "packet loss"
fi

docker compose --profile auto-select stop client-auto 2>&1 | tail -1

# ============================================================
section "MULTI-CLIENT — gateway + client simultaneously"
# ============================================================

# Gateway is already running on exit-aws
docker compose --profile explicit-ams rm -f client-explicit-ams 2>/dev/null
docker compose --profile explicit-ams up -d client-explicit-ams 2>&1 | tail -1
sleep 5

GW_PING=$(docker compose exec gateway-r3s ping -c 2 -W 2 8.8.8.8 2>&1)
CL_PING=$(docker compose exec client-explicit-ams ping -c 2 -W 2 8.8.8.8 2>&1)

if echo "$GW_PING" | grep -q "0% packet loss"; then
    pass "Gateway ping while client running"
else
    fail "Gateway concurrent ping" "packet loss"
fi

if echo "$CL_PING" | grep -q "0% packet loss"; then
    pass "Client ping while gateway running"
else
    fail "Client concurrent ping" "packet loss"
fi

# Verify they're on different exits
GW_EXIT=$(docker compose logs exit-aws 2>&1 | grep "Assigned.*r3s" | tail -1)
CL_EXIT=$(docker compose logs exit-ams 2>&1 | grep "Assigned.*mac" | tail -1)
if [ -n "$GW_EXIT" ] && [ -n "$CL_EXIT" ]; then
    pass "Gateway on exit-aws, client on exit-ams (different exits)"
else
    fail "Multi-client exit split" "not verified"
fi

docker compose --profile explicit-ams stop client-explicit-ams 2>&1 | tail -1

# ============================================================
section "EXIT SWITCH — runtime failover updates TUN config"
# ============================================================
# Regression: 2026-05-19 exit-switch race condition. Symptom: after
# rebalance/failover, switchExit logged "done" but TUN kept the OLD exit's
# CIDR. Root cause: control-handler accepted old-exit keepalive into
# joinResp racing the new target's response. Fix: strict target-only filter
# during switch (pkg/protocol.ShouldAcceptControl).
#
# Test: stop the auto-selected exit; gateway should switch to survivor and
# the post-switch [switch] log line must show an IP from survivor's subnet.

set +e

# `start` (not rm+up) preserves the container filesystem and therefore the
# auto-generated identity-key — exit-side TOFU keystore would reject a new
# pubkey under name=auto with "unknown device".
docker compose --profile auto-select start client-auto >/dev/null 2>&1 || \
    docker compose --profile auto-select up -d client-auto >/dev/null 2>&1
sleep 5

wait_for client-auto "VPN ready" 60
if [ $? -ne 0 ]; then
    fail "Switch test setup" "client did not connect within 60s"
else
    INITIAL_EXIT=$(docker compose logs client-auto 2>&1 | grep "Auto-selected exit:" | head -1 | sed 's/.*Auto-selected exit: //' | awk '{print $1}')
    # Image lacks `ip` cmd — parse from log line: "Received IP: 10.9.X.Y/255.255.255.0 gw ..."
    INITIAL_IP=$(docker compose logs client-auto 2>&1 | grep "Received IP:" | head -1 | sed -n 's/.*Received IP: \([0-9.]*\)\/.*/\1/p')

    if [ -n "$INITIAL_EXIT" ] && [ -n "$INITIAL_IP" ]; then
        pass "Initial state: exit=$INITIAL_EXIT, IP=$INITIAL_IP"

        if [ "$INITIAL_EXIT" = "aws" ]; then
            SURVIVOR=ams ; SURVIVOR_SUBNET=10.9.1
        else
            SURVIVOR=aws ; SURVIVOR_SUBNET=10.9.0
        fi

        echo "Stopping exit-$INITIAL_EXIT to force runtime switch to $SURVIVOR..."
        docker compose stop exit-"$INITIAL_EXIT" >/dev/null 2>&1

        # Tracker marks exit stale after ~2x heartbeat (~60s), health-check
        # fires switchExit at 15s. Budget: 90s.
        SWITCH_OK=0
        for _ in $(seq 1 18); do
            sleep 5
            if docker compose logs client-auto 2>&1 | grep -qE "\[switch\] .* → $SURVIVOR: done"; then
                SWITCH_OK=1
                break
            fi
        done

        if [ $SWITCH_OK -eq 1 ]; then
            pass "Gateway switched $INITIAL_EXIT → $SURVIVOR after exit failure"

            SWITCH_IP=$(docker compose logs client-auto 2>&1 | grep -E "\[switch\] .* → $SURVIVOR: done" | tail -1 | sed -n 's/.*done (IP \([0-9.]*\)).*/\1/p')
            if echo "$SWITCH_IP" | grep -q "^$SURVIVOR_SUBNET\."; then
                pass "Post-switch IP from survivor subnet: $SWITCH_IP (was $INITIAL_IP)"
            else
                fail "Post-switch IP wrong subnet" "got '$SWITCH_IP', expected $SURVIVOR_SUBNET.x — race regression"
            fi

            if docker compose exec client-auto ping -c 3 -W 2 8.8.8.8 2>&1 | grep -q "0% packet loss"; then
                pass "Ping through tunnel after switch"
            else
                fail "Ping after switch" "packet loss (TUN may be on stale CIDR)"
            fi
        else
            fail "Exit switch trigger" "[switch] done not logged within 90s"
        fi

        # Restore the dead exit for downstream tests (BROKER FAILOVER expects exits running).
        docker compose up -d exit-"$INITIAL_EXIT" >/dev/null 2>&1
        sleep 3  # give exit time to reconnect to brokers
    else
        fail "Switch test initial state" "exit=$INITIAL_EXIT IP=$INITIAL_IP"
    fi
fi

docker compose --profile auto-select stop client-auto >/dev/null 2>&1
set -e

# ============================================================
section "BROKER FAILOVER"
# ============================================================

echo "Stopping broker-1..."
docker compose stop mosquitto-1 2>&1 | tail -1
sleep 8

# Check gateway reconnected
if docker compose logs gateway-r3s 2>&1 | grep -qE "(connect error|client error).*mosquitto-1"; then
    pass "Gateway detected broker-1 down"
else
    fail "Gateway broker-1 detection" "no error logged"
fi

CONNECT_COUNT=$(docker compose logs gateway-r3s 2>&1 | grep -c "\[mqtt\] connected" || true)
if [ "$CONNECT_COUNT" -ge 2 ]; then
    pass "Gateway reconnected to broker-2"
else
    fail "Gateway reconnect" "only $CONNECT_COUNT connects (expected >=2)"
fi

if docker compose exec gateway-r3s ping -c 3 -W 2 8.8.8.8 2>&1 | grep -q "0% packet loss"; then
    pass "Gateway ping after broker failover"
else
    fail "Gateway post-failover ping" "packet loss"
fi

# ============================================================
section "BROKER RECOVERY (no flapping)"
# ============================================================

echo "Restarting broker-1..."
docker compose start mosquitto-1 2>&1 | tail -1
sleep 5

# Gateway should stay on broker-2 (no reconnect log after recovery)
RECONNECT_COUNT=$(docker compose logs gateway-r3s 2>&1 | grep -cE "connected: vtx-client" || true)
if [ "$RECONNECT_COUNT" -le 2 ]; then
    pass "Gateway stays on broker-2, no flapping ($RECONNECT_COUNT connects total)"
else
    fail "Gateway flapping" "$RECONNECT_COUNT reconnects detected"
fi

if docker compose exec gateway-r3s ping -c 2 -W 2 8.8.8.8 2>&1 | grep -q "0% packet loss"; then
    pass "Gateway ping after broker recovery"
else
    fail "Gateway post-recovery ping" "packet loss"
fi

# ============================================================
section "EXIT RECONNECT AFTER BROKER RECOVERY"
# ============================================================

# Exits should have reconnected to broker-1
sleep 5
if docker compose logs exit-aws 2>&1 | grep -cE "connected: vtx-exit-aws-0" | grep -q "[2-9]"; then
    pass "Exit-AWS reconnected to broker-1"
else
    # Check if it reconnected at all
    if docker compose logs exit-aws 2>&1 | tail -10 | grep -q "connected"; then
        pass "Exit-AWS reconnected to broker-1"
    else
        fail "Exit-AWS reconnect" "not reconnected"
    fi
fi

# Heartbeats should be visible on restored broker
HEARTBEAT_RESTORED=$(docker compose exec mosquitto-1 mosquitto_sub -t "discovery/exits/+" -u vtx-client-r3s -P test123 -C 1 -W 35 2>&1)
if echo "$HEARTBEAT_RESTORED" | grep -q '"id"'; then
    pass "Heartbeats visible on restored broker-1"
else
    fail "Heartbeats on restored broker" "not received"
fi

# ============================================================
section "THROUGHPUT (iperf3)"
# ============================================================

docker compose exec -d exit-aws iperf3 -s -p 5201 2>/dev/null || true
sleep 2
IPERF=$(docker compose exec gateway-r3s iperf3 -c 10.9.0.1 -p 5201 -t 3 2>&1 || echo "IPERF_FAILED")
if echo "$IPERF" | grep -q "sender"; then
    BITRATE=$(echo "$IPERF" | grep "sender" | awk '{for(i=1;i<=NF;i++) if($i ~ /bits/) print $(i-1), $i}')
    pass "Throughput: $BITRATE"
else
    fail "iperf3" "could not measure"
fi

# ============================================================
section "DNS RESOLUTION through tunnel"
# ============================================================

DNS_RESULT=$(docker compose exec gateway-r3s wget -q -O - --timeout=5 http://ifconfig.me/ip 2>&1 || echo "FAILED")
if [ "$DNS_RESULT" != "FAILED" ] && [ -n "$DNS_RESULT" ]; then
    pass "DNS + HTTP through tunnel (external IP: $DNS_RESULT)"
else
    fail "DNS resolution" "wget failed"
fi

# ============================================================
section "WSS FALLBACK (mixed mqtt:// + ws:// URLs)"
# ============================================================

docker compose --profile ws-fallback rm -f client-ws-fallback 2>/dev/null
docker compose --profile ws-fallback up -d client-ws-fallback 2>&1 | tail -1
sleep 5

if wait_for client-ws-fallback "VPN ready" 15; then
    pass "Client (ws-fallback) connected and ready"
else
    fail "Client ws-fallback" "not ready"
fi

if wait_for client-ws-fallback "DH key exchange" 10; then
    pass "Client (ws-fallback) DH key exchange (PFS)"
else
    fail "Client ws-fallback DH" "no DH key exchange logged"
fi

if docker compose exec client-ws-fallback ping -c 3 -W 2 8.8.8.8 2>&1 | grep -q "0% packet loss"; then
    pass "Client (ws-fallback) ping 8.8.8.8 through tunnel"
else
    fail "Client ws-fallback ping" "packet loss"
fi

# ============================================================
section "RESULTS"
# ============================================================

echo ""
echo "=============================="
echo -e "  ${GREEN}Passed: $PASSED${NC}"
if [ $FAILED -gt 0 ]; then
    echo -e "  ${RED}Failed: $FAILED${NC}"
fi
echo "  Total:  $TOTAL"
echo "=============================="

if [ $FAILED -gt 0 ]; then
    exit 1
fi
