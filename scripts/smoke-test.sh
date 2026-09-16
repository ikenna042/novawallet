#!/usr/bin/env bash
# End-to-end check against a running instance (docker compose up, or dotnet run).
# Usage: ./scripts/smoke-test.sh [base-url]     (requires curl and jq)
# Uses the seeded admin: ADMIN_EMAIL / ADMIN_PASSWORD (compose demo defaults if unset).
set -euo pipefail

BASE="${1:-${BASE_URL:-http://localhost:8080}}"
ADMIN_EMAIL="${ADMIN_EMAIL:-admin@novawallet.local}"
ADMIN_PASSWORD="${ADMIN_PASSWORD:-ChangeMe-Admin-2026!}"
PASSWORD="Smoke-Test-Passw0rd"
RUN="$(date +%s)$RANDOM"   # digits only: safe inside emails and NIP references

pass() { printf '  \033[32m✔\033[0m %s\n' "$1"; }
fail() { printf '  \033[31m✘\033[0m %s\n' "$1"; exit 1; }
expect() { [[ "$2" == "$3" ]] && pass "$1" || fail "$1 (expected '$3', got '$2')"; }
uuid() { uuidgen 2>/dev/null || python3 -c 'import uuid; print(uuid.uuid4())'; }
# Build JSON with jq rather than inline "{\"a\":1,...}" strings: inside "$( )" the old bash 3.2 that
# ships with macOS brace-expands the commas and splits the body into several arguments.
json() { jq -nc "$@"; }
transfer_body() { json --arg s "$1" --arg d "$2" --argjson a "$3" '{sourceWalletId:$s,destinationWalletId:$d,amountKobo:$a}'; }

echo "Waiting for $BASE/health/ready ..."
for _ in $(seq 1 60); do
  curl -sf "$BASE/health/ready" >/dev/null && break
  sleep 2
done
curl -sf "$BASE/health/ready" >/dev/null || fail "service is not ready"
pass "service is ready"

post() { # post PATH BODY [TOKEN] [extra curl args...]
  local path=$1 body=$2 tok=${3:-}
  shift $(( $# < 3 ? $# : 3 ))
  curl -s -X POST "$BASE$path" -H 'Content-Type: application/json' \
    ${tok:+-H "Authorization: Bearer $tok"} -d "$body" "$@"
}
get() { # get PATH TOKEN [extra curl args...]
  local path=$1 tok=$2
  shift 2
  curl -s "$BASE$path" -H "Authorization: Bearer $tok" "$@"
}
login() { post /api/v1/auth/login "$(json --arg e "$1" --arg p "$2" '{email:$e,password:$p}')"; }

# ---------- authentication ----------
ALICE_EMAIL="alice-$RUN@example.test"
BOB_EMAIL="bob-$RUN@example.test"
expect "Alice registers (201)" \
  "$(post /api/v1/auth/register "$(json --arg e "$ALICE_EMAIL" --arg p "$PASSWORD" '{email:$e,password:$p,fullName:"Alice Smoke"}')" '' -o /dev/null -w '%{http_code}')" "201"
post /api/v1/auth/register "$(json --arg e "$BOB_EMAIL" --arg p "$PASSWORD" '{email:$e,password:$p}')" > /dev/null

expect "wrong password is refused (invalid_credentials)" \
  "$(login "$ALICE_EMAIL" "Not-the-password-1" | jq -r .code)" "invalid_credentials"

ALICE_SESSION=$(login "$ALICE_EMAIL" "$PASSWORD")
ALICE=$(jq -r .data.accessToken <<<"$ALICE_SESSION")
BOB=$(login "$BOB_EMAIL" "$PASSWORD" | jq -r .data.accessToken)
ADMIN=$(login "$ADMIN_EMAIL" "$ADMIN_PASSWORD" | jq -r .data.accessToken)
[[ "$ALICE" == ey* && "$BOB" == ey* ]] && pass "Alice and Bob signed in" || fail "customer sign-in"
[[ "$ADMIN" == ey* ]] && pass "admin signed in" || fail "admin sign-in (check ADMIN_EMAIL / ADMIN_PASSWORD)"

REFRESH_BODY=$(jq -c '.data | {refreshToken}' <<<"$ALICE_SESSION")
REFRESHED=$(post /api/v1/auth/refresh "$REFRESH_BODY")
ALICE=$(jq -r .data.accessToken <<<"$REFRESHED")
expect "refresh token rotates and the new access token works" "$(get /api/v1/auth/me "$ALICE" | jq -r .data.email)" "$ALICE_EMAIL"
expect "the old refresh token can't be reused" \
  "$(post /api/v1/auth/refresh "$REFRESH_BODY" | jq -r .code)" "invalid_refresh_token"

# ---------- wallets & money ----------
A_CREATED=$(post /api/v1/wallets '{}' "$ALICE")
A=$(jq -r .data.walletId <<<"$A_CREATED")
B=$(post /api/v1/wallets '{}' "$BOB" | jq -r .data.walletId)
[[ "$A" =~ ^[0-9a-f-]{36}$ && "$B" =~ ^[0-9a-f-]{36}$ ]] && pass "created wallets $A / $B" || fail "wallet creation"
expect "success responses are { statusCode, message, data }" \
  "$(jq -r '[(keys_unsorted[0:3] | join(",")), .statusCode, .message] | map(tostring) | join(" ")' <<<"$A_CREATED")" "statusCode,message,data 201 Wallet created"

expect "admin credits ₦10,000 to Alice (simulated NIP)" \
  "$(post "/api/v1/wallets/$A/credit" "$(json --arg r "NIP$RUN" '{amountKobo:1000000,reference:$r,narration:"Inbound NIP"}')" "$ADMIN" -o /dev/null -w '%{http_code}')" "201"
expect "customer cannot credit (403)" \
  "$(post "/api/v1/wallets/$A/credit" "$(json --arg r "NIPX$RUN" '{amountKobo:1000000,reference:$r}')" "$ALICE" -o /dev/null -w '%{http_code}')" "403"

KEY=$(uuid)
BODY=$(transfer_body "$A" "$B" 250000)
OTHER_BODY=$(transfer_body "$A" "$B" 999)
expect "transferred ₦2,500 Alice → Bob" \
  "$(post /api/v1/transfers "$BODY" "$ALICE" -H "Idempotency-Key: $KEY" -o /dev/null -w '%{http_code}')" "201"
replayed=$(post /api/v1/transfers "$BODY" "$ALICE" -H "Idempotency-Key: $KEY" -D - -o /dev/null | tr -d '\r' | awk -F': ' 'tolower($1)=="idempotent-replayed"{print $2}')
expect "same Idempotency-Key is replayed, not re-executed" "$replayed" "true"
expect "same key with a different body is rejected" \
  "$(post /api/v1/transfers "$OTHER_BODY" "$ALICE" -H "Idempotency-Key: $KEY" | jq -r .code)" "idempotency_key_reused"
expect "overdraft by 1 kobo is refused" \
  "$(post /api/v1/transfers "$(transfer_body "$B" "$A" 250001)" "$BOB" -H "Idempotency-Key: $(uuid)" | jq -r .code)" "insufficient_funds"

expect "Alice's balance is ₦7,500" "$(get "/api/v1/wallets/$A/balance" "$ALICE" | jq -r .data.balanceKobo)" "750000"
expect "Bob's balance is ₦2,500" "$(get "/api/v1/wallets/$B/balance" "$BOB" | jq -r .data.balanceKobo)" "250000"
expect "Alice's statement has 2 entries, newest first" \
  "$(get "/api/v1/wallets/$A/statement" "$ALICE" | jq -r '[.data.items[].direction] | join(",")')" "Debit,Credit"
expect "Bob cannot read Alice's wallet (404)" "$(get "/api/v1/wallets/$A/balance" "$BOB" -o /dev/null -w '%{http_code}')" "404"
expect "audit hash chain is intact" "$(get "/api/v1/wallets/$A/audit" "$ADMIN" | jq -r .data.chainIntact)" "true"

# ---------- administration ----------
expect "customer cannot use admin endpoints (403)" "$(get /api/v1/admin/users "$ALICE" -o /dev/null -w '%{http_code}')" "403"
expect "admin finds Alice and her wallet" \
  "$(get "/api/v1/admin/users?email=$ALICE_EMAIL" "$ADMIN" | jq -r '.data.items[0].walletId')" "$A"

post "/api/v1/admin/wallets/$A/freeze" '{"reason":"Smoke test hold"}' "$ADMIN" > /dev/null
expect "frozen wallet cannot send (wallet_frozen)" \
  "$(post /api/v1/transfers "$(transfer_body "$A" "$B" 100)" "$ALICE" -H "Idempotency-Key: $(uuid)" | jq -r .code)" "wallet_frozen"
expect "frozen wallet can still receive" \
  "$(post /api/v1/transfers "$(transfer_body "$B" "$A" 100)" "$BOB" -H "Idempotency-Key: $(uuid)" -o /dev/null -w '%{http_code}')" "201"
post "/api/v1/admin/wallets/$A/unfreeze" '{}' "$ADMIN" > /dev/null
expect "after unfreezing, Alice can send again" \
  "$(post /api/v1/transfers "$(transfer_body "$A" "$B" 100)" "$ALICE" -H "Idempotency-Key: $(uuid)" -o /dev/null -w '%{http_code}')" "201"

BOB_ID=$(get /api/v1/auth/me "$BOB" | jq -r .data.userId)
post "/api/v1/admin/users/$BOB_ID/disable" '{"reason":"Smoke test"}' "$ADMIN" > /dev/null
expect "disabled user's existing token stops working (401)" "$(get /api/v1/auth/me "$BOB" -o /dev/null -w '%{http_code}')" "401"
post "/api/v1/admin/users/$BOB_ID/enable" '{}' "$ADMIN" > /dev/null
expect "re-enabled user can sign in again" "$(login "$BOB_EMAIL" "$PASSWORD" | jq -r .data.user.status)" "Active"
expect "admin actions were logged" \
  "$(get '/api/v1/admin/actions?limit=4' "$ADMIN" | jq -r '[.data.items[].action] | join(",")')" "USER_ENABLED,USER_DISABLED,WALLET_UNFROZEN,WALLET_FROZEN"

# ---------- platform ----------
expect "no token → 401" "$(curl -s -o /dev/null -w '%{http_code}' "$BASE/api/v1/wallets/$A/balance")" "401"
expect "errors are the same envelope plus RFC 7807 fields" \
  "$(curl -s "$BASE/api/v1/wallets/$A/balance" | jq -r '[(keys_unsorted[0:3] | join(",")), .statusCode, .code, (.data == null), .status] | map(tostring) | join(" ")')" \
  "statusCode,message,data 401 unauthorized true 401"
expect "OpenAPI spec is served" "$(curl -s -o /dev/null -w '%{http_code}' "$BASE/swagger/v1/swagger.json")" "200"

echo "All smoke checks passed."
