# Testing guide: how to exercise the API, and which requirement each check proves

Three ways to test, from quickest to deepest:

| Way | Time | Proves |
|---|---|---|
| **A. Smoke script** (`./scripts/smoke-test.sh`) | 5 s | The main flow works end to end |
| **B. By hand**, in Swagger or with curl (sections 2–4) | 10–15 min | Each requirement, one at a time, in front of an audience |
| **C. Automated suite** (`dotnet test`, section 5) | ~15 s | Everything, including 200-request concurrency |

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

### 1.1 Getting tokens

Every endpoint needs a JWT. Compose enables a **mock issuer** at `POST /dev/token`. There are two roles:

| Role | Can do |
|---|---|
| `customer` | Create **their own** wallet, read it, transfer **from** it |
| `operator` | Credit any wallet (simulates the NIP settlement system), read the audit trail |

**In Swagger:**
1. Open **Dev → POST /dev/token → Try it out**.
2. Send `{"subject":"alice","role":"customer"}` and copy the `accessToken`.
3. Click **Authorize** (top right), paste the token and click **Authorize**. Every call you try now runs as Alice.
4. To act as someone else, click **Authorize → Logout** and paste a different token.

**With curl.** Paste this block once per terminal; the rest of the guide uses these variables:

```bash
BASE=http://localhost:8080
tok() { curl -s -X POST $BASE/dev/token -H 'Content-Type: application/json' \
          -d "{\"subject\":\"$1\",\"role\":\"${2:-customer}\"}" | jq -r .accessToken; }
RUN=$RANDOM                       # makes names unique, so you can repeat the guide
ALICE=$(tok alice$RUN)
BOB=$(tok bob$RUN)
OPS=$(tok ops$RUN operator)
```

---

## 2. Functional requirements (brief §2.1)

### R1. Create wallet: *"for a customer id; starting balance zero"*

```bash
A=$(curl -s -X POST $BASE/api/v1/wallets -H "Authorization: Bearer $ALICE" | tee /dev/stderr | jq -r .walletId)
B=$(curl -s -X POST $BASE/api/v1/wallets -H "Authorization: Bearer $BOB"   | jq -r .walletId)
```

Expected: **201**, with `"balanceKobo":0` and `"currency":"NGN"`. The customer id is taken from the token.

| Also try | Expected |
|---|---|
| Create a second wallet as Alice | **409** `wallet_already_exists` (one wallet per customer) |
| As Alice, body `{"customerId":"someone-else"}` | **403** `forbidden` |
| As OPS, body `{"customerId":"carol"}` | **201** (operators may create wallets for anyone) |

Swagger: **Wallets → POST /api/v1/wallets**, body `{}`.

### R2. Get balance: *"current balance and currency (NGN), amounts in kobo"*

```bash
curl -s $BASE/api/v1/wallets/$A/balance -H "Authorization: Bearer $ALICE" | jq
```

Expected: `balanceKobo` (an integer), `currency: "NGN"`, and a human-readable `balanceDisplay` such as `"₦1,000.00"`.

| Also try | Expected |
|---|---|
| Bob reads Alice's balance (`-H "Authorization: Bearer $BOB"`) | **404** (other customers' wallets are hidden, so wallet ids can't be guessed) |

### R3. Credit wallet: *"simulating an inbound NIP transfer"*

```bash
curl -s -X POST $BASE/api/v1/wallets/$A/credit -H "Authorization: Bearer $OPS" \
  -H 'Content-Type: application/json' \
  -d "{\"amountKobo\":100000,\"reference\":\"NIP${RUN}0001\",\"narration\":\"Salary\"}" | jq
```

Expected: **201**, and the receipt shows `balanceAfterKobo: 100000` (₦1,000).

| Also try | Expected | Why |
|---|---|---|
| The exact same command again | **201**, same `transactionId`, header `Idempotent-Replayed: true`, balance still ₦1,000 | The NIP session reference de-duplicates bank retries |
| Same reference, `amountKobo` changed | **409** `duplicate_reference` | A reference can't be reused for a different credit |
| The same credit using `$ALICE`'s token | **403** | Only the settlement operator can create money |

To see the response headers, add `-i` to curl.

### R4. Transfer: *"atomically … concurrency-safe … never negative … no double-spend"*

Single transfer of ₦250:

```bash
curl -si -X POST $BASE/api/v1/transfers -H "Authorization: Bearer $ALICE" \
  -H 'Content-Type: application/json' -H "Idempotency-Key: $(uuidgen)" \
  -d "{\"sourceWalletId\":\"$A\",\"destinationWalletId\":\"$B\",\"amountKobo\":25000,\"narration\":\"Lunch\"}"
```

Expected: **201**. `balanceAfterKobo` is **Alice's** balance only; a sender never sees the recipient's balance.

**Live concurrency demo.** First top Alice back up to exactly ₦1,000 in a fresh wallet: create `ALICE2` and `A2` with the R1 and R3 commands, crediting `100000`. Then fire **15 transfers of ₦100 at the same instant**:

```bash
seq 1 15 | xargs -P 15 -I{} sh -c "curl -s -o /dev/null -w '%{http_code}\n' -X POST $BASE/api/v1/transfers \
  -H 'Authorization: Bearer $ALICE2' -H 'Content-Type: application/json' -H \"Idempotency-Key: \$(uuidgen)\" \
  -d '{\"sourceWalletId\":\"$A2\",\"destinationWalletId\":\"$B\",\"amountKobo\":10000}'" | sort | uniq -c

curl -s $BASE/api/v1/wallets/$A2/balance -H "Authorization: Bearer $ALICE2" | jq .balanceKobo
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

**How it's safe:** both wallet rows are locked with `SELECT … FOR UPDATE`, lower id first to avoid deadlocks, before the balance is read. See README §4.

### R5. Idempotency: *"replay must not double-process; same key + different payload rejected"*

```bash
KEY=$(uuidgen)
BODY="{\"sourceWalletId\":\"$A\",\"destinationWalletId\":\"$B\",\"amountKobo\":5000}"
send() { curl -si -X POST $BASE/api/v1/transfers -H "Authorization: Bearer $ALICE" \
          -H 'Content-Type: application/json' -H "Idempotency-Key: $KEY" -d "$1" | grep -iE '^HTTP|replayed|"code"|transactionId'; }

send "$BODY"                      # 201, Idempotent-Replayed: false
send "$BODY"                      # 201, Idempotent-Replayed: true, SAME transactionId
send "${BODY/5000/9999}"          # 422 idempotency_key_reused
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
CAROL=$(tok carol$RUN)
C=$(curl -s -X POST $BASE/api/v1/wallets -H "Authorization: Bearer $CAROL" | jq -r .walletId)
curl -s -X POST $BASE/api/v1/wallets/$C/credit -H "Authorization: Bearer $OPS" -H 'Content-Type: application/json' \
  -d "{\"amountKobo\":100000000,\"reference\":\"NIP${RUN}0009\"}" > /dev/null          # ₦1,000,000

xfer() { curl -s -X POST $BASE/api/v1/transfers -H "Authorization: Bearer $CAROL" -H 'Content-Type: application/json' \
  -H "Idempotency-Key: $(uuidgen)" -d "{\"sourceWalletId\":\"$C\",\"destinationWalletId\":\"$B\",\"amountKobo\":$1}" | jq -c '{amountKobo, code, detail}'; }

xfer 30000000     # ₦300,000 → ok
xfer 20000000     # ₦200,000 → ok (exactly at the limit)
xfer 1            # 1 kobo   → 422 daily_limit_exceeded, "Remaining today: ₦0.00"
```

Carol still has ₦500,000, so this was refused by the limit, not by her balance.

The **midnight WAT reset** can't be shown by hand without waiting until 00:00 Lagos time. It is covered by tests that control the clock:
- `Daily_limit_resets_at_midnight_west_africa_time` (integration: 23:59:59 is refused, 00:00 is allowed);
- `DailyLimitPolicyTests` (unit: UTC↔WAT boundaries, including New Year).

### R8. Audit log: *"every balance mutation … append-only, immutable … queryable"*

Through the API (operator only):

```bash
curl -s $BASE/api/v1/wallets/$A/audit -H "Authorization: Bearer $OPS" | jq '{chainIntact, records: [.records[] | {action, deltaKobo, balanceBeforeKobo, balanceAfterKobo, actor, correlationId}]}'
```

Expected: one record per credit or debit, and `chainIntact: true`. As a customer (`$ALICE`) the same call returns **403**.

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

## 3. Hard constraints (brief §2.2)

| Constraint | How to see it | Expected |
|---|---|---|
| **Integers in kobo only** | Transfer with `"amountKobo":100.5` or `"amountKobo":"100"` | **400**, `"$.amountKobo": ["The input was not valid."]` |
| | Look at any response | Every amount is a whole number of kobo |
| **Never negative** | R4 concurrency demo (balance ends at exactly 0) | |
| | `docker compose exec db psql -U novawallet -d novawallet -c "UPDATE wallets SET balance_kobo = -1;"` | Refused by `ck_wallets_balance_non_negative` |
| **JWT bearer auth** | `curl -si $BASE/api/v1/wallets/$A/balance` (no token) | **401** |
| | Same call with `-H "Authorization: Bearer abc.def.ghi"` | **401** |
| | A customer calling `/credit` or `/audit` | **403** |
| **Structured errors (RFC 7807)** | Any error above | `Content-Type: application/problem+json` with `type`, `title`, `status`, `detail`, a stable **`code`**, `traceId` and `correlationId` |
| **`docker compose up`** | Section 1 | The stack starts with one command. CI also runs it on every push: the **compose-smoke** job on GitHub Actions |

---

## 4. Stretch goals (brief §2.4)

### S1. Rate limiting on transfers (20 per minute per customer)

```bash
DAVE=$(tok dave$RUN); D=$(curl -s -X POST $BASE/api/v1/wallets -H "Authorization: Bearer $DAVE" | jq -r .walletId)
for i in $(seq 1 25); do
  curl -s -o /dev/null -w '%{http_code} ' -X POST $BASE/api/v1/transfers -H "Authorization: Bearer $DAVE" \
    -H 'Content-Type: application/json' -H "Idempotency-Key: $(uuidgen)" \
    -d "{\"sourceWalletId\":\"$D\",\"destinationWalletId\":\"$B\",\"amountKobo\":1}"
done; echo
```

Expected:
- 20 responses of `422` (Dave has no money, but the requests got through the limiter), then `429 429 429 429 429`.
- The 429 responses carry a `Retry-After` header and `code: rate_limited`.
- Other customers are unaffected.

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
curl -s $BASE/api/v1/wallets/$A/audit -H "Authorization: Bearer $OPS" | jq '.records[-1].correlationId'
```

Expected: the same ID appears in the response header, in every JSON log line for that request, and in the audit record. Without the header, the W3C trace id is used instead.

### S4. Health and readiness probes

```bash
curl -s $BASE/health/live            # Healthy: the process is up
curl -s $BASE/health/ready | jq      # {"status":"Healthy","checks":{"database":"Healthy"}}
docker compose stop db; curl -s -w ' %{http_code}\n' $BASE/health/ready; docker compose start db
```

Expected: with the database stopped, readiness returns **503 Unhealthy** while liveness stays healthy.

---

## 5. Automated tests (Way C)

You need the .NET 8 SDK. With Docker running (OrbStack), the integration tests start their own PostgreSQL:

```bash
dotnet test                                                     # everything: 91 tests
dotnet test tests/NovaWallet.UnitTests                          # 45 unit tests, no database needed
dotnet test --filter "FullyQualifiedName~ConcurrencyTests" \
  --logger "console;verbosity=detailed"                         # prints the timings
```

If `dotnet` isn't on your PATH (it was installed user-locally), use `~/.dotnet/dotnet test`.

### Requirement → automated test map

| Requirement | Test(s) |
|---|---|
| R1 Create wallet | `New_wallet_has_zero_ngn_balance`, `Second_wallet_for_same_customer_is_a_conflict`, `Customer_cannot_create_a_wallet_for_someone_else_but_operator_can` |
| R2 Balance | `New_wallet_has_zero_ngn_balance`, `Customer_cannot_see_another_customers_wallet` |
| R3 Credit | `Credit_is_idempotent_on_the_nip_reference`, `Customers_cannot_credit_wallets` |
| R4 Transfer and concurrency | `Transfer_moves_money_and_returns_only_the_senders_balance`, **`Parallel_transfers_from_one_wallet_never_overdraw_or_double_spend`** (200 requests), `Opposing_transfers_between_two_wallets_do_not_deadlock_and_conserve_money`, `Many_senders_into_one_wallet_all_land`, `Customer_cannot_transfer_out_of_someone_elses_wallet`, unit `Wallets_are_locked_in_ascending_id_order_regardless_of_direction` |
| R5 Idempotency | `Replayed_transfer_returns_the_same_receipt_and_moves_nothing`, `Reusing_a_key_with_a_different_payload_is_rejected`, **`Concurrent_retries_with_the_same_idempotency_key_move_money_exactly_once`**, `Rejected_transfer_replays_the_same_error_even_after_a_top_up`, `Idempotency_keys_are_scoped_per_customer`, `Transfer_without_idempotency_key_is_rejected` |
| R6 Statement | `Statement_is_paginated_newest_first_without_gaps_or_duplicates`, `Statement_rejects_bad_paging_parameters` |
| R7 Daily limit | `Daily_limit_resets_at_midnight_west_africa_time`, **`Concurrent_transfers_cannot_exceed_the_daily_limit`**, unit `DailyLimitPolicyTests`, `Daily_limit_counts_earlier_transfers_and_resets_at_wat_midnight` |
| R8 Audit log | `Audit_trail_records_every_mutation_with_an_intact_hash_chain`, `Ledger_and_audit_tables_are_append_only_in_the_database`, unit `AuditChainTests` |
| Kobo integers | `Malformed_transfer_requests_are_rejected_with_problem_details`, unit `MoneyTests` |
| Never negative | Concurrency tests above, `Database_refuses_a_negative_balance_even_if_the_application_tried` |
| JWT | `Requests_without_a_valid_token_are_rejected_with_problem_details` (no token, wrong key, garbage, `alg:none`), `Expired_tokens_are_rejected`, `Dev_token_endpoint_issues_usable_tokens_and_validates_input` |
| Problem Details | Every rejection test calls `ReadProblemAsync`, which asserts `application/problem+json` and reads `code` |
| docker compose | GitHub Actions job **compose-smoke** |
| S1 Rate limiting | `Transfer_endpoint_is_rate_limited_per_customer` |
| S2 Outbox | `Transfer_completed_event_is_published_from_the_outbox` |
| S3 Correlation IDs | `Audit_trail_records_every_mutation_with_an_intact_hash_chain` (sends `X-Correlation-ID` and checks the response header and the audit record) |
| S4 Health | `Health_and_openapi_endpoints_are_public` |
| OpenAPI reachable | `Health_and_openapi_endpoints_are_public` (`/swagger/v1/swagger.json`) |

---

## 6. Suggested 5-minute live demo for the panel

1. Run `docker compose up --build`, then open **/swagger**.
2. `./scripts/smoke-test.sh`: all core requirements in 5 seconds.
3. **The R4 concurrency demo**: *10 × 201, 5 × 422, balance 0*.
4. **The R5 idempotency** sequence: *replayed: true, then 422 on a changed body*.
5. **The R8 tamper attempt** in psql: *append-only error*.
6. `dotnet test --filter ConcurrencyTests`: the 200-request version.

Reset between rehearsals: `docker compose down -v && docker compose up -d`.
