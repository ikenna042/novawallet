#!/usr/bin/env bash
# End-to-end check against a running instance (docker compose up, or dotnet run).
# Usage: ./scripts/smoke-test.sh [base-url]     (requires curl and jq)
set -euo pipefail

BASE="${1:-${BASE_URL:-http://localhost:8080}}"
RUN="$(date +%s)"

pass() { printf '  \033[32m✔\033[0m %s\n' "$1"; }
fail() { printf '  \033[31m✘\033[0m %s\n' "$1"; exit 1; }
expect() { [[ "$2" == "$3" ]] && pass "$1" || fail "$1 (expected '$3', got '$2')"; }
uuid() { uuidgen 2>/dev/null || python3 -c 'import uuid; print(uuid.uuid4())'; }

echo "Waiting for $BASE/health/ready ..."
for _ in $(seq 1 60); do
  curl -sf "$BASE/health/ready" >/dev/null && break
  sleep 2
done
curl -sf "$BASE/health/ready" >/dev/null || fail "service did not become ready"
pass "service is ready"

token() {
  curl -sf -X POST "$BASE/dev/token" -H 'Content-Type: application/json' \
    -d "{\"subject\":\"$1\",\"role\":\"$2\"}" | jq -r .accessToken
}
api() { # api METHOD PATH TOKEN [BODY] [extra curl args...]
  local method=$1 path=$2 tok=$3 body=${4:-}
  shift $(( $# < 4 ? $# : 4 ))
  curl -s -X "$method" "$BASE$path" -H "Authorization: Bearer $tok" -H 'Content-Type: application/json' \
    ${body:+-d "$body"} "$@"
}

ALICE=$(token "alice-$RUN" customer)
BOB=$(token "bob-$RUN" customer)
OPS=$(token "ops-$RUN" operator)
pass "issued tokens (mock issuer)"

A=$(api POST /api/v1/wallets "$ALICE" '{}' | jq -r .walletId)
B=$(api POST /api/v1/wallets "$BOB" '{}' | jq -r .walletId)
[[ "$A" =~ ^[0-9a-f-]{36}$ && "$B" =~ ^[0-9a-f-]{36}$ ]] && pass "created wallets $A / $B" || fail "wallet creation"

status=$(api POST "/api/v1/wallets/$A/credit" "$OPS" "{\"amountKobo\":1000000,\"reference\":\"NIP$RUN\",\"narration\":\"Inbound NIP\"}" -o /dev/null -w '%{http_code}')
expect "operator credited ₦10,000 to Alice" "$status" "201"

status=$(api POST "/api/v1/wallets/$A/credit" "$ALICE" "{\"amountKobo\":1000000,\"reference\":\"NIPX$RUN\"}" -o /dev/null -w '%{http_code}')
expect "customer cannot credit (403)" "$status" "403"

KEY=$(uuid)
BODY="{\"sourceWalletId\":\"$A\",\"destinationWalletId\":\"$B\",\"amountKobo\":250000,\"narration\":\"Smoke test\"}"
status=$(api POST /api/v1/transfers "$ALICE" "$BODY" -H "Idempotency-Key: $KEY" -o /dev/null -w '%{http_code}')
expect "transferred ₦2,500 Alice → Bob" "$status" "201"

replayed=$(api POST /api/v1/transfers "$ALICE" "$BODY" -H "Idempotency-Key: $KEY" -D - -o /dev/null | tr -d '\r' | awk -F': ' 'tolower($1)=="idempotent-replayed"{print $2}')
expect "same Idempotency-Key is replayed, not re-executed" "$replayed" "true"

code=$(api POST /api/v1/transfers "$ALICE" "${BODY/250000/999}" -H "Idempotency-Key: $KEY" | jq -r .code)
expect "same key with different body is rejected" "$code" "idempotency_key_reused"

code=$(api POST /api/v1/transfers "$BOB" "{\"sourceWalletId\":\"$B\",\"destinationWalletId\":\"$A\",\"amountKobo\":250001}" -H "Idempotency-Key: $(uuid)" | jq -r .code)
expect "overdraft by 1 kobo is refused" "$code" "insufficient_funds"

expect "Alice balance is ₦7,500" "$(api GET "/api/v1/wallets/$A/balance" "$ALICE" "" | jq -r .balanceKobo)" "750000"
expect "Bob balance is ₦2,500" "$(api GET "/api/v1/wallets/$B/balance" "$BOB" "" | jq -r .balanceKobo)" "250000"
expect "Alice statement has 2 entries, newest first" \
  "$(api GET "/api/v1/wallets/$A/statement" "$ALICE" "" | jq -r '[.items[].direction] | join(",")')" "Debit,Credit"
expect "Bob cannot read Alice's wallet (404)" \
  "$(api GET "/api/v1/wallets/$A/balance" "$BOB" "" -o /dev/null -w '%{http_code}')" "404"
expect "audit hash chain is intact" "$(api GET "/api/v1/wallets/$A/audit" "$OPS" "" | jq -r .chainIntact)" "true"
expect "no token → 401" "$(curl -s -o /dev/null -w '%{http_code}' "$BASE/api/v1/wallets/$A/balance")" "401"
expect "OpenAPI spec is served" "$(curl -s -o /dev/null -w '%{http_code}' "$BASE/swagger/v1/swagger.json")" "200"

echo "All smoke checks passed."
