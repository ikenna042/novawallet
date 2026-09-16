# Testing guide: how to exercise the API, and which requirement each check proves

Three ways to test, from quickest to deepest:

| Way | Time | Proves |
|---|---|---|
| **A. Smoke script** (`./scripts/smoke-test.sh`) | 5 s | The main flow works end to end, including sign-in and admin actions (30 checks) |
| **B. By hand**, in Swagger or with curl (sections 2–5) | 15–20 min | Each requirement, one at a time, in front of an audience |
| **C. Automated suite** (`dotnet test`, section 6) | ~20 s | Everything (148 tests), including 200-request concurrency |

---

## 1. Start the service

```bash
cd ~/Documents/projects/others/novawallet-ledger
docker compose up --build        # leave this running; open a second terminal for the rest
```

Checks:
- http://localhost:8080/health/ready should show `Healthy`.
- http://localhost:8080/swagger should show the API documentation.

Quick end-to-end check (Way A):

```bash
./scripts/smoke-test.sh
```

### 1.1 Accounts and tokens

Every endpoint except sign-up, sign-in, refresh, health and Swagger needs a JWT. There are two roles:

| Role | How you get it | Can do |
|---|---|---|
| `customer` | Anyone can register | Create **their own** wallet, read it, transfer **from** it |
| `admin` | Seeded at startup: **`admin@novawallet.local` / `ChangeMe-Admin-2026!`** (demo only; set `ADMIN_EMAIL` / `ADMIN_PASSWORD` in `.env` to change), or promoted by another admin | Credit any wallet (simulates NIP settlement), view any wallet, read audit trails, manage users, freeze wallets |

**In Swagger:**
1. Open **Auth → POST /api/v1/auth/register → Try it out** and send `{"email":"alice@example.com","password":"Demo-Passw0rd"}`.
2. Open **POST /api/v1/auth/login**, send the same email and password, and copy the `accessToken`.
3. Click **Authorize** (top right), paste the token and click **Authorize**. Every call you try now runs as Alice.
4. To act as the admin, click **Authorize → Logout**, sign in as `admin@novawallet.local`, and paste that token instead.

Access tokens last **15 minutes**. After that, sign in again or use `POST /api/v1/auth/refresh`.

**With curl.** Paste this block once per terminal (and again if you start getting 401s after 15 minutes); the rest of the guide uses these variables:

```bash
BASE=http://localhost:8080
PW='Demo-Passw0rd'
RUN=$RANDOM                                   # makes emails unique, so you can repeat the guide
login()  { curl -s -X POST $BASE/api/v1/auth/login -H 'Content-Type: application/json' \
             -d "{\"email\":\"$1\",\"password\":\"$2\"}"; }
signup() { curl -s -X POST $BASE/api/v1/auth/register -H 'Content-Type: application/json' \
             -d "{\"email\":\"$1-$RUN@example.com\",\"password\":\"$PW\"}" > /dev/null
           login "$1-$RUN@example.com" "$PW" | jq -r .accessToken; }
ALICE=$(signup alice)
BOB=$(signup bob)
ADMIN=$(login admin@novawallet.local 'ChangeMe-Admin-2026!' | jq -r .accessToken)
```

Sign-up, sign-in and refresh are rate-limited to 20 per minute per client in the compose setup. If you see `429 rate_limited`, wait a minute.

---

## 2. Authentication and administration

### AU1. Register and sign in

```bash
curl -s -X POST $BASE/api/v1/auth/register -H 'Content-Type: application/json' \
  -d "{\"email\":\"dayo-$RUN@example.com\",\"password\":\"$PW\",\"fullName\":\"Dayo\"}" | jq
login "dayo-$RUN@example.com" "$PW" | jq '{tokenType, expiresIn, user: .user.role, refreshToken: (.refreshToken[0:12] + "…")}'
curl -s $BASE/api/v1/auth/me -H "Authorization: Bearer $ALICE" | jq
```

Expected:
- Register returns **201** with `role: "customer"` (public sign-up never creates admins).
- Login returns a Bearer token with `expiresIn: 900` (15 minutes) and a refresh token.
- `me` shows the caller's profile and `walletId` (null until a wallet exists).

| Also try | Expected |
|---|---|
| Register the same email again (any letter case) | **409** `email_already_registered` |
| Password `short1`, `onlyletters`, or `1234567890123` | **400** `validation_error` (10–128 characters, with a letter and a digit) |
| Register with an extra `"role":"admin"` field | **400**: unknown fields are refused |
| Sign in with a wrong password, then with an email that doesn't exist | Both **401** with an identical body (`invalid_credentials`), so emails can't be probed |
| Five wrong passwords in a row, then the right one | Still **401**: the account is locked for 15 minutes |

### AU2. Refresh, reuse detection, logout

```bash
S1=$(login "dayo-$RUN@example.com" "$PW")
R1=$(jq -r .refreshToken <<<"$S1")
S2=$(curl -s -X POST $BASE/api/v1/auth/refresh -H 'Content-Type: application/json' -d "{\"refreshToken\":\"$R1\"}")
jq '{newAccessToken: (.accessToken[0:12] + "…")}' <<<"$S2"                     # 200: rotated

curl -s -X POST $BASE/api/v1/auth/refresh -H 'Content-Type: application/json' -d "{\"refreshToken\":\"$R1\"}" | jq .code
# "invalid_refresh_token": R1 was already used, so this looks like theft...
R2=$(jq -r .refreshToken <<<"$S2")
curl -s -X POST $BASE/api/v1/auth/refresh -H 'Content-Type: application/json' -d "{\"refreshToken\":\"$R2\"}" | jq .code
# "invalid_refresh_token": ...and the whole session was revoked, including R2
```

Logout: `POST /api/v1/auth/logout` with `{"refreshToken": "..."}` and the access token returns **204**, after which that refresh token no longer works.

### AD1. Admin can find anyone and see any wallet

```bash
A=$(curl -s -X POST $BASE/api/v1/wallets -H "Authorization: Bearer $ALICE" | jq -r .walletId)
B=$(curl -s -X POST $BASE/api/v1/wallets -H "Authorization: Bearer $BOB"   | jq -r .walletId)

curl -s "$BASE/api/v1/admin/users?email=alice-$RUN" -H "Authorization: Bearer $ADMIN" | jq '.items[] | {userId, email, role, status, walletId}'
curl -s $BASE/api/v1/wallets/$A/balance -H "Authorization: Bearer $ADMIN" | jq
curl -s -o /dev/null -w '%{http_code}\n' $BASE/api/v1/admin/users -H "Authorization: Bearer $ALICE"      # 403
```

### AD2. Disable a user: access stops immediately

```bash
BOB_ID=$(curl -s $BASE/api/v1/auth/me -H "Authorization: Bearer $BOB" | jq -r .userId)
curl -s -X POST $BASE/api/v1/admin/users/$BOB_ID/disable -H "Authorization: Bearer $ADMIN" \
  -H 'Content-Type: application/json' -d '{"reason":"Suspected account takeover"}' | jq '{status, disabledReason}'

curl -s -o /dev/null -w '%{http_code}\n' $BASE/api/v1/auth/me -H "Authorization: Bearer $BOB"   # 401, although the token hasn't expired
login "bob-$RUN@example.com" "$PW" | jq .code                                                       # "invalid_credentials"

curl -s -X POST $BASE/api/v1/admin/users/$BOB_ID/enable -H "Authorization: Bearer $ADMIN" | jq .status   # "Active"
BOB=$(login "bob-$RUN@example.com" "$PW" | jq -r .accessToken)                                      # Bob signs in again
```

How it works: every token carries a `ver` claim. Disabling a user (or changing their role) increments their `token_version`, and each request checks it against the database.

### AD3. Promote and demote

```bash
curl -s -X POST $BASE/api/v1/admin/users/$BOB_ID/role -H "Authorization: Bearer $ADMIN" \
  -H 'Content-Type: application/json' -d '{"role":"admin"}' | jq .role              # "admin"
curl -s -o /dev/null -w '%{http_code}\n' $BASE/api/v1/auth/me -H "Authorization: Bearer $BOB"   # 401: old token is dead
BOB=$(login "bob-$RUN@example.com" "$PW" | jq -r .accessToken)
curl -s -o /dev/null -w '%{http_code}\n' $BASE/api/v1/admin/users -H "Authorization: Bearer $BOB"   # 200: Bob is an admin now

BOB_ADMIN=$BOB
curl -s -X POST $BASE/api/v1/admin/users/$BOB_ID/role -H "Authorization: Bearer $BOB_ADMIN" \
  -H 'Content-Type: application/json' -d '{"role":"customer"}' | jq .code             # "admin_rule_violation": can't demote yourself
curl -s -X POST $BASE/api/v1/admin/users/$BOB_ID/role -H "Authorization: Bearer $ADMIN" \
  -H 'Content-Type: application/json' -d '{"role":"customer"}' | jq .role              # "customer"
BOB=$(login "bob-$RUN@example.com" "$PW" | jq -r .accessToken)
```

The last active admin can never be removed. That case is covered by a unit test, because in the API an admin can't demote themselves.

### AD4. Freeze a wallet (debit hold)

```bash
curl -s -X POST $BASE/api/v1/wallets/$A/credit -H "Authorization: Bearer $ADMIN" -H 'Content-Type: application/json' \
  -d "{\"amountKobo\":500000,\"reference\":\"NIP${RUN}0100\"}" > /dev/null                  # Alice gets ₦5,000
curl -s -X POST $BASE/api/v1/admin/wallets/$A/freeze -H "Authorization: Bearer $ADMIN" \
  -H 'Content-Type: application/json' -d '{"reason":"Chargeback dispute #4471"}' | jq '{status, frozenReason}'

send() { curl -s -o /dev/null -w "%{http_code}\n" -X POST $BASE/api/v1/transfers -H "Authorization: Bearer $1" \
  -H 'Content-Type: application/json' -H "Idempotency-Key: $(uuidgen)" \
  -d "{\"sourceWalletId\":\"$2\",\"destinationWalletId\":\"$3\",\"amountKobo\":$4}"; }
send "$ALICE" "$A" "$B" 100     # 422: wallet_frozen
curl -s -X POST $BASE/api/v1/wallets/$A/credit -H "Authorization: Bearer $ADMIN" -H 'Content-Type: application/json' \
  -d "{\"amountKobo\":100,\"reference\":\"NIP${RUN}0101\"}" -o /dev/null -w '%{http_code}\n'   # 201: money can still arrive

curl -s -X POST $BASE/api/v1/admin/wallets/$A/unfreeze -H "Authorization: Bearer $ADMIN" | jq .status   # "Active"
send "$ALICE" "$A" "$B" 100     # 201
```

### AD5. Admin action log

```bash
curl -s "$BASE/api/v1/admin/actions?limit=6" -H "Authorization: Bearer $ADMIN" | jq '.items[] | {action, targetType, detail, actorId}'
docker compose exec db psql -U novawallet -d novawallet -c "UPDATE admin_actions SET detail = 'nothing to see';"
# ERROR:  admin_actions is append-only: UPDATE is not allowed
```

---

## 3. Functional requirements (brief §2.1)

Run these in order; they reuse `$ALICE`, `$BOB`, `$ADMIN`, `$A` and `$B` from the steps above. If you skipped section 2, first create the wallets with the two `A=` / `B=` lines from AD1.

### R1. Create wallet: *"for a customer id; starting balance zero"*

```bash
CAROL=$(signup carol)
curl -s -X POST $BASE/api/v1/wallets -H "Authorization: Bearer $CAROL" | jq
```

Expected: **201**, with `"balanceKobo":0`, `"currency":"NGN"` and `"status":"Active"`. The customer id is the signed-in user's id.

| Also try | Expected |
|---|---|
| Create a second wallet as Carol | **409** `wallet_already_exists` (one wallet per customer) |
| As Carol, body `{"customerId":"<someone else's userId without dashes>"}` | **403** `forbidden` |
| As admin, `{"customerId":"<a registered user's id without dashes>"}` for a user with no wallet | **201** |
| As admin, a `customerId` that isn't a registered user | **400** |

### R2. Get balance: *"current balance and currency (NGN), amounts in kobo"*

```bash
curl -s $BASE/api/v1/wallets/$A/balance -H "Authorization: Bearer $ALICE" | jq
```

Expected: `balanceKobo` (an integer), `currency: "NGN"`, and a human-readable `balanceDisplay` such as `"₦5,000.00"`.

| Also try | Expected |
|---|---|
| Bob reads Alice's balance (`-H "Authorization: Bearer $BOB"`) | **404** (other customers' wallets are hidden, so wallet ids can't be guessed) |
| The admin reads it | **200** |

### R3. Credit wallet: *"simulating an inbound NIP transfer"*

```bash
curl -s -X POST $BASE/api/v1/wallets/$A/credit -H "Authorization: Bearer $ADMIN" \
  -H 'Content-Type: application/json' \
  -d "{\"amountKobo\":100000,\"reference\":\"NIP${RUN}0001\",\"narration\":\"Salary\"}" | jq
```

Expected: **201**, and the receipt's `balanceAfterKobo` has gone up by 100000 (₦1,000).

| Also try | Expected | Why |
|---|---|---|
| The exact same command again | **201**, same `transactionId`, header `Idempotent-Replayed: true`, balance unchanged | The NIP session reference de-duplicates bank retries |
| Same reference, `amountKobo` changed | **409** `duplicate_reference` | A reference can't be reused for a different credit |
| The same credit using `$ALICE`'s token | **403** | Only the admin (settlement) role can create money |

To see the response headers, add `-i` to curl.

### R4. Transfer: *"atomically … concurrency-safe … never negative … no double-spend"*

Single transfer of ₦250:

```bash
curl -si -X POST $BASE/api/v1/transfers -H "Authorization: Bearer $ALICE" \
  -H 'Content-Type: application/json' -H "Idempotency-Key: $(uuidgen)" \
  -d "{\"sourceWalletId\":\"$A\",\"destinationWalletId\":\"$B\",\"amountKobo\":25000,\"narration\":\"Lunch\"}"
```

Expected: **201**. `balanceAfterKobo` is **Alice's** balance only; a sender never sees the recipient's balance.

**Live concurrency demo.** Give a fresh customer exactly ₦1,000, then fire **15 transfers of ₦100 at the same instant**:

```bash
DEMO=$(signup demo)
D=$(curl -s -X POST $BASE/api/v1/wallets -H "Authorization: Bearer $DEMO" | jq -r .walletId)
curl -s -X POST $BASE/api/v1/wallets/$D/credit -H "Authorization: Bearer $ADMIN" -H 'Content-Type: application/json' \
  -d "{\"amountKobo\":100000,\"reference\":\"NIP${RUN}0002\"}" > /dev/null

seq 1 15 | xargs -P 15 -I{} sh -c "curl -s -o /dev/null -w '%{http_code}\n' -X POST $BASE/api/v1/transfers \
  -H 'Authorization: Bearer $DEMO' -H 'Content-Type: application/json' -H \"Idempotency-Key: \$(uuidgen)\" \
  -d '{\"sourceWalletId\":\"$D\",\"destinationWalletId\":\"$B\",\"amountKobo\":10000}'" | sort | uniq -c

curl -s $BASE/api/v1/wallets/$D/balance -H "Authorization: Bearer $DEMO" | jq .balanceKobo
```

Expected (verified):

```
  10 201
   5 422      ← insufficient_funds
0             ← balance: exactly zero, never negative
```

Only 15 requests are used because the transfer endpoint is rate-limited to 20 per minute per customer (see S1). The automated test goes much further: it fires 200 requests.

| Also try | Expected |
|---|---|
| `destinationWalletId` equal to `sourceWalletId` | **400** `same_wallet_transfer` |
| Bob sends from Alice's wallet | **404** (Bob can't touch a wallet he doesn't own) |
| Amount 1 kobo more than the balance | **422** `insufficient_funds` |
| Sending from a frozen wallet | **422** `wallet_frozen` (see AD4) |

**How it's safe:** both wallet rows are locked with `SELECT … FOR UPDATE`, lower id first to avoid deadlocks, before the balance is read. See README §4.

### R5. Idempotency: *"replay must not double-process; same key + different payload rejected"*

```bash
KEY=$(uuidgen)
BODY="{\"sourceWalletId\":\"$A\",\"destinationWalletId\":\"$B\",\"amountKobo\":5000}"
OTHER="{\"sourceWalletId\":\"$A\",\"destinationWalletId\":\"$B\",\"amountKobo\":9999}"
replay() { curl -si -X POST $BASE/api/v1/transfers -H "Authorization: Bearer $ALICE" \
          -H 'Content-Type: application/json' -H "Idempotency-Key: $KEY" -d "$1" | grep -iE '^HTTP|replayed|"code"|transactionId' | cut -c1-90; }

replay "$BODY"                    # 201, Idempotent-Replayed: false
replay "$BODY"                    # 201, Idempotent-Replayed: true, SAME transactionId
replay "$OTHER"                   # 422 idempotency_key_reused
curl -s $BASE/api/v1/wallets/$A/balance -H "Authorization: Bearer $ALICE" | jq .balanceKobo   # debited once
```

| Also try | Expected |
|---|---|
| No `Idempotency-Key` header | **400** `validation_error` |
| A key that already got `insufficient_funds`, retried after a top-up | The same **422** again. Declines are remembered; use a new key for a new attempt |
| Bob uses the same key string as Alice | Works. Keys are scoped per customer |

Swagger: **Transfers → POST /api/v1/transfers** has an `Idempotency-Key` field. Execute twice with the same value.

### R6. Statement: *"paginated transaction history, newest first"*

```bash
curl -s "$BASE/api/v1/wallets/$A/statement?limit=2" -H "Authorization: Bearer $ALICE" | jq
# copy "nextCursor" from the output, then:
curl -s "$BASE/api/v1/wallets/$A/statement?limit=2&cursor=<nextCursor>" -H "Authorization: Bearer $ALICE" | jq
```

Expected:
- Items are newest first, each with `direction` (Debit/Credit), `amountKobo`, `balanceAfterKobo`, `counterpartyWalletId`, `reference` and `narration`.
- `nextCursor` is `null` on the last page.
- `limit=0`, `limit=101` or `cursor=abc` all give **400**.

### R7. Daily limit: *"₦500,000/day per wallet, reset at midnight WAT"*

```bash
RICH=$(signup rich)
C=$(curl -s -X POST $BASE/api/v1/wallets -H "Authorization: Bearer $RICH" | jq -r .walletId)
curl -s -X POST $BASE/api/v1/wallets/$C/credit -H "Authorization: Bearer $ADMIN" -H 'Content-Type: application/json' \
  -d "{\"amountKobo\":100000000,\"reference\":\"NIP${RUN}0009\"}" > /dev/null          # ₦1,000,000

xfer() { curl -s -X POST $BASE/api/v1/transfers -H "Authorization: Bearer $RICH" -H 'Content-Type: application/json' \
  -H "Idempotency-Key: $(uuidgen)" -d "{\"sourceWalletId\":\"$C\",\"destinationWalletId\":\"$B\",\"amountKobo\":$1}" | jq -c '{amountKobo, code, detail}'; }

xfer 30000000     # ₦300,000 → ok
xfer 20000000     # ₦200,000 → ok (exactly at the limit)
xfer 1            # 1 kobo   → 422 daily_limit_exceeded, "Remaining today: ₦0.00"
```

This customer still has ₦500,000, so the last transfer was refused by the limit, not by the balance.

The **midnight WAT reset** can't be shown by hand without waiting until 00:00 Lagos time. It is covered by tests that control the clock:
- `Daily_limit_resets_at_midnight_west_africa_time` (integration: 23:59:59 is refused, 00:00 is allowed);
- `DailyLimitPolicyTests` (unit: UTC↔WAT boundaries, including New Year).

### R8. Audit log: *"every balance mutation … append-only, immutable … queryable"*

Through the API (admin only):

```bash
curl -s $BASE/api/v1/wallets/$A/audit -H "Authorization: Bearer $ADMIN" | jq '{chainIntact, records: [.records[] | {action, deltaKobo, balanceBeforeKobo, balanceAfterKobo, actor, correlationId}]}'
```

Expected: one record per credit or debit, and `chainIntact: true`. As a customer (`$ALICE`) the same call returns **403**. The `actor` is the id of the user who made the change.

Directly in the database, the way a reviewer would query it:

```bash
docker compose exec db psql -U novawallet -d novawallet \
  -c "SELECT id, action, delta_kobo, balance_before_kobo, balance_after_kobo, actor FROM audit_log ORDER BY id DESC LIMIT 5;"

# Try to tamper: the database refuses
docker compose exec db psql -U novawallet -d novawallet -c "UPDATE audit_log SET delta_kobo = 0;"
docker compose exec db psql -U novawallet -d novawallet -c "DELETE FROM ledger_entries;"
```

Expected: `ERROR: audit_log is append-only: UPDATE is not allowed` (and the same for `ledger_entries`).
From a desktop SQL client, connect to `localhost:5433`, database and user `novawallet`, password `novawallet-local-only`.

---

## 4. Hard constraints (brief §2.2)

| Constraint | How to see it | Expected |
|---|---|---|
| **Integers in kobo only** | Transfer with `"amountKobo":100.5` or `"amountKobo":"100"` | **400**, `"$.amountKobo": ["The input was not valid."]` |
| | Look at any response | Every amount is a whole number of kobo |
| **Never negative** | R4 concurrency demo (balance ends at exactly 0) | |
| | `docker compose exec db psql -U novawallet -d novawallet -c "UPDATE wallets SET balance_kobo = -1;"` | Refused by `ck_wallets_balance_non_negative` |
| **JWT bearer auth** | `curl -si $BASE/api/v1/wallets/$A/balance` (no token) | **401** |
| | Same call with `-H "Authorization: Bearer abc.def.ghi"` | **401** |
| | A token from a disabled user, or from before a role change (AD2, AD3) | **401** |
| | A customer calling `/credit`, `/audit` or `/admin/...` | **403** |
| **Structured errors (RFC 7807)** | Any error above | `Content-Type: application/problem+json` with `type`, `title`, `status`, `detail`, a stable **`code`**, `traceId` and `correlationId` |
| **`docker compose up`** | Section 1 | The stack starts with one command and seeds the admin. CI also runs it on every push: the **compose-smoke** job on GitHub Actions |

---

## 5. Stretch goals (brief §2.4)

### S1. Rate limiting

Transfers: 20 per minute per customer.

```bash
EVE=$(signup eve); E=$(curl -s -X POST $BASE/api/v1/wallets -H "Authorization: Bearer $EVE" | jq -r .walletId)
for i in $(seq 1 25); do
  curl -s -o /dev/null -w '%{http_code} ' -X POST $BASE/api/v1/transfers -H "Authorization: Bearer $EVE" \
    -H 'Content-Type: application/json' -H "Idempotency-Key: $(uuidgen)" \
    -d "{\"sourceWalletId\":\"$E\",\"destinationWalletId\":\"$B\",\"amountKobo\":1}"
done; echo
```

Expected:
- 20 responses of `422` (Eve has no money, but the requests got through the limiter), then `429 429 429 429 429`.
- The 429 responses carry a `Retry-After` header and `code: rate_limited`.
- Other customers are unaffected.

Sign-in: 20 per minute per client IP in compose (10 by default outside compose). Run `login nobody@example.com wrong` in a loop 25 times; the last attempts get **429**.

### S2. Outbox → `TransferCompleted` event

```bash
docker compose exec db psql -U novawallet -d novawallet \
  -c "SELECT type, occurred_at, processed_at IS NOT NULL AS published FROM outbox_messages ORDER BY occurred_at DESC LIMIT 5;"
docker compose logs api | grep "Published" | tail -3
```

Expected:
- One `wallet.transfer.completed.v1` row per transfer, written in the same transaction as the transfer.
- `published = t` within about 2 seconds.
- The publisher logs each event; it stands in for Kafka or Service Bus.

### S3. Structured logging with correlation IDs

```bash
curl -s -o /dev/null -D - -X POST $BASE/api/v1/transfers -H "Authorization: Bearer $ALICE" \
  -H 'Content-Type: application/json' -H "Idempotency-Key: $(uuidgen)" -H 'X-Correlation-ID: demo-ussd-session-42' \
  -d "{\"sourceWalletId\":\"$A\",\"destinationWalletId\":\"$B\",\"amountKobo\":100}" | grep -i correlation

docker compose logs api | grep demo-ussd-session-42
curl -s $BASE/api/v1/wallets/$A/audit -H "Authorization: Bearer $ADMIN" | jq '.records[-1].correlationId'
```

Expected: the same ID appears in the response header, in every JSON log line for that request, and in the audit record. Admin actions record it too (AD5). Without the header, the W3C trace id is used instead.

### S4. Health and readiness probes

```bash
curl -s $BASE/health/live            # Healthy: the process is up
curl -s $BASE/health/ready | jq      # {"status":"Healthy","checks":{"database":"Healthy"}}
docker compose stop db; curl -s -w ' %{http_code}\n' $BASE/health/ready; docker compose start db
```

Expected: with the database stopped, readiness returns **503 Unhealthy** while liveness stays healthy.

---

## 6. Automated tests (Way C)

You need the .NET 8 SDK. With Docker running (OrbStack), the integration tests start their own PostgreSQL:

```bash
dotnet test                                                     # everything: 148 tests
dotnet test tests/NovaWallet.UnitTests                          # 74 unit tests, no database needed
dotnet test --filter "FullyQualifiedName~ConcurrencyTests" \
  --logger "console;verbosity=detailed"                         # prints the timings
```

If `dotnet` isn't on your PATH (it was installed user-locally), use `~/.dotnet/dotnet test`.

### Requirement → automated test map

| Requirement | Test(s) |
|---|---|
| R1 Create wallet | `New_wallet_has_zero_ngn_balance`, `Second_wallet_for_same_customer_is_a_conflict`, `Customer_cannot_create_a_wallet_for_someone_else_but_admin_can` |
| R2 Balance | `New_wallet_has_zero_ngn_balance`, `Customer_cannot_see_another_customers_wallet`, `Admin_can_find_a_user_and_view_their_wallet` |
| R3 Credit | `Credit_is_idempotent_on_the_nip_reference`, `Customers_cannot_credit_wallets` |
| R4 Transfer and concurrency | `Transfer_moves_money_and_returns_only_the_senders_balance`, **`Parallel_transfers_from_one_wallet_never_overdraw_or_double_spend`** (200 requests), `Opposing_transfers_between_two_wallets_do_not_deadlock_and_conserve_money`, `Many_senders_into_one_wallet_all_land`, `Customer_cannot_transfer_out_of_someone_elses_wallet`, unit `Wallets_are_locked_in_ascending_id_order_regardless_of_direction` |
| R5 Idempotency | `Replayed_transfer_returns_the_same_receipt_and_moves_nothing`, `Reusing_a_key_with_a_different_payload_is_rejected`, **`Concurrent_retries_with_the_same_idempotency_key_move_money_exactly_once`**, `Rejected_transfer_replays_the_same_error_even_after_a_top_up`, `Idempotency_keys_are_scoped_per_customer`, `Transfer_without_idempotency_key_is_rejected` |
| R6 Statement | `Statement_is_paginated_newest_first_without_gaps_or_duplicates`, `Statement_rejects_bad_paging_parameters` |
| R7 Daily limit | `Daily_limit_resets_at_midnight_west_africa_time`, **`Concurrent_transfers_cannot_exceed_the_daily_limit`**, unit `DailyLimitPolicyTests`, `Daily_limit_counts_earlier_transfers_and_resets_at_wat_midnight` |
| R8 Audit log | `Audit_trail_records_every_mutation_with_an_intact_hash_chain`, `Ledger_and_audit_tables_are_append_only_in_the_database`, unit `AuditChainTests` |
| Kobo integers | `Malformed_transfer_requests_are_rejected_with_problem_details`, unit `MoneyTests` |
| Never negative | Concurrency tests above, `Database_refuses_a_negative_balance_even_if_the_application_tried` |
| JWT | `Requests_without_a_valid_token_are_rejected_with_problem_details` (no token, wrong key, garbage, `alg:none`), `Expired_tokens_are_rejected`, `Correctly_signed_token_for_a_user_that_does_not_exist_is_rejected`, `Token_claiming_a_role_the_user_does_not_have_is_rejected` |
| AU Sign-up / sign-in | `Register_login_and_me_round_trip_as_a_customer`, `Public_registration_cannot_create_an_admin`, `Duplicate_email_is_a_conflict_regardless_of_case`, `Weak_passwords_are_rejected`, `Wrong_password_and_unknown_email_are_indistinguishable`, `Account_locks_after_five_failed_logins`, unit `CredentialsTests`, `AuthServiceTests` |
| AU Sessions | `Refresh_rotates_tokens_and_reusing_an_old_token_revokes_the_session`, **`Concurrent_refreshes_with_the_same_token_have_exactly_one_winner`**, `Logout_ends_the_session`, unit `Expired_refresh_token_is_rejected`, `Separate_logins_are_separate_sessions` |
| AD Admin | `Customers_and_anonymous_callers_cannot_use_admin_endpoints`, `User_list_is_paginated_by_email`, `Disabling_a_user_cuts_off_access_immediately_and_enabling_restores_it`, `Promotion_invalidates_old_tokens_and_takes_effect_on_next_sign_in`, `Admin_cannot_disable_themselves`, `Admin_requests_are_validated`, `Frozen_wallet_cannot_send_but_can_receive_until_unfrozen`, `Freeze_requires_a_reason_and_an_existing_wallet`, `Admin_actions_are_logged_and_the_log_is_append_only`, `Admin_seeding_is_idempotent_across_restarts`, unit `The_last_active_admin_cannot_be_disabled_or_demoted` |
| Problem Details | Every rejection test calls `ReadProblemAsync`, which asserts `application/problem+json` and reads `code` |
| docker compose | GitHub Actions job **compose-smoke** |
| S1 Rate limiting | `Transfer_endpoint_is_rate_limited_per_customer`, `Sign_in_endpoints_are_rate_limited_per_client` |
| S2 Outbox | `Transfer_completed_event_is_published_from_the_outbox` |
| S3 Correlation IDs | `Audit_trail_records_every_mutation_with_an_intact_hash_chain` (sends `X-Correlation-ID` and checks the response header and the audit record) |
| S4 Health | `Health_and_openapi_endpoints_are_public` |
| OpenAPI reachable | `Health_and_openapi_endpoints_are_public` (`/swagger/v1/swagger.json`) |

---

## 7. Suggested 6-minute live demo for the panel

1. Run `docker compose up --build`, then open **/swagger**.
2. `./scripts/smoke-test.sh`: sign-in, money movement and admin actions in 5 seconds.
3. **AU1**: register and sign in; wrong password and unknown email look identical.
4. **The R4 concurrency demo**: *10 × 201, 5 × 422, balance 0*.
5. **The R5 idempotency** sequence: *replayed: true, then 422 on a changed body*.
6. **AD2**: disable a user, and their unexpired token dies on the next request.
7. **The R8 tamper attempt** in psql: *append-only error*.
8. `dotnet test --filter ConcurrencyTests`: the 200-request version.

Reset between rehearsals: `docker compose down -v && docker compose up -d`.
