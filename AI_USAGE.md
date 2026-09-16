# AI usage

This task was built with an AI coding agent working under my direction. This file records what it did, what I asked for, and, most importantly, where its output was wrong or naive for a financial system and how that was caught.

## Tools and what they were used for

| Tool | Used for |
|---|---|
| **Claude Code** (desktop app, Claude Opus 5 model) | Reading the brief, proposing the plan, scaffolding the solution, writing code, tests, Dockerfile, CI, README and the presentation deck. It also ran the builds, the tests and a local PostgreSQL. |
| .NET 8 SDK, xUnit, Testcontainers | Checking every change the AI made: build with warnings-as-errors, 74 unit tests, 74 integration tests against real PostgreSQL 16. |
| OrbStack (Docker) | Running `docker compose up` locally and the smoke script against it. |

My role was to set scope and constraints, make the judgement calls, and require evidence (tests and deliberately broken code) before accepting a claim.

## Prompts I gave and what came back

1. **The brief itself.** I pasted the recruiter's email and attached the take-home PDF.
   - **What came back:** a plan, not code. The agent noticed that the email asks for a PowerPoint while the PDF asks for a Git repo, and asked which I wanted. It found that .NET and Docker were not installed and asked how to handle that. It then proposed:
     - integer kobo;
     - `SELECT … FOR UPDATE` with a fixed lock order;
     - idempotency keys claimed with `INSERT … ON CONFLICT` inside the same transaction;
     - a per-wallet hash-chained audit table protected by triggers;
     - an outbox, plus tests that fire 200 concurrent requests.
   - **My decisions:** I chose "code repo + deck", and I approved the plan before any code was written.

2. **"Docker is too heavy, how can I continue without it"**
   - **What came back:** install the .NET 8 SDK user-locally with Microsoft's `dotnet-install.sh` (after asking me first). Make the integration tests use Testcontainers when Docker exists (CI), otherwise a connection string in `NOVAWALLET_TEST_DB`. Let GitHub Actions prove that `docker compose up` works.
   - **My decisions:** for a local database I chose the `@embedded-postgres/darwin-arm64` binaries (PostgreSQL 16.14) run from a scratch folder, so nothing was installed system-wide.

3. **Choosing the local database:** I answered the agent's question with **"npm embedded-postgres"**.
   - **What came back:** it downloaded the package into a scratch folder and initialised a TCP-only PostgreSQL 16.14 server (its first attempt failed because the macOS socket path was too long). It then ran all 46 integration tests against that server.
   - **Unprompted extra step:** after the suite went green, the agent deleted `FOR UPDATE`, re-ran the concurrency tests and restored it, then did the same with the lock ordering. This showed the tests fail for the right reason. I kept the results as evidence (section 7 below and the README).

4. **"I want guide on how to test the api and how it covers the requirements"**
   - **What came back:** `docs/TESTING_GUIDE.md`, with every command executed against the running stack before it was written down. That run exposed the leak described in case 4 below.

5. **"Why do I have to use /dev/token to create token… I want to have real users authentication flow including admin flow"**
   - **What came back:** first an explanation (the brief allows a mock issuer, and the mock tokens were already real, fully validated JWTs), then a design with choices for me to make.
   - **My decisions:** auth built into the service rather than a Keycloak container (to keep my 8 GB laptop light); the first admin seeded from environment variables; `/dev/token` removed; admins can credit wallets, read audit trails, manage users, view any wallet and freeze wallets.
   - The agent then built register / login / refresh / logout and the admin endpoints, growing the suite from 91 to 148 tests, and repeated the "break it on purpose" check on the new security code (section 7).

## Where the AI was wrong or naive, and how it was caught

### 1. Error responses silently stopped being Problem Details (caught by a test)
The generated controllers carried `[Produces("application/json")]`. That looks harmless, but it overrides the content type of ASP.NET's automatic 400 validation responses. So a request with `"amountKobo": 100.5` came back as `application/json` instead of `application/problem+json`, which broke the "consistent structured errors" constraint.

- **How it was caught:** `Malformed_transfer_requests_are_rejected_with_problem_details` asserts the media type, and 5 of its 7 cases failed on the first run.
- **Fix:** removed the attribute (JSON is the only formatter anyway).

### 2. Every declined transfer was logged as an unhandled server error (caught by reading logs)
The first version turned business rejections (insufficient funds, daily limit) into Problem Details through a global `IExceptionHandler`. The tests all passed. But running the service and reading its log showed that .NET 8's exception middleware logs **every** exception that reaches it at `Error` level, with a full stack trace, before any handler runs.

In a bank that means every declined transfer looks like an incident. It would flood alerting and hide real failures.

- **Fix:** a `DomainExceptionFilter` handles expected rejections inside MVC and logs them at `Information`. The global handler remains only for genuinely unexpected failures.
- **Re-check:** after the fix, a full smoke run produced **0** error-level log lines.

### 3. Tests that "checked" the wrong rule (caught when they failed for an unexpected reason)
Twice the AI wrote a check for *insufficient funds* using an amount that also exceeded the **daily limit**:
- a unit test used ₦2,000,000;
- the smoke script used ₦999,999.99.

The limit check runs first, so the service correctly answered `daily_limit_exceeded`, and the assertions failed. The service was right; the tests were naive about rule ordering. Worse, a test like this could pass for the wrong reason if the expectation were loosened to "any 422".

- **Fix:** the insufficient-funds cases now overdraw by exactly one kobo (or spend from an empty wallet), well under the limit.

### 4. Error messages leaked internal class names (caught while writing the testing guide)
Sending `"amountKobo": 100.5` returned a validation error that included the full .NET type name (`NovaWallet.Api.Controllers.TransfersController+TransferRequest`). That's a small information leak about the service's internals.

- **How it was caught:** by actually running each command before putting it in the guide and reading the response.
- **Fix:** `AllowInputFormatterExceptionMessages = false`. The error now says only which field is invalid ("The input was not valid.").

### 5. A smoke check that passed for the wrong reason (caught by the next check failing)
For the admin flow, the AI rewrote `scripts/smoke-test.sh` and passed inline JSON bodies (`"{\"amountKobo\":1000000,...}"`) inside `"$( … )"`. The bash 3.2 that ships with macOS applies brace expansion there. So each `{…,…}` body turned into **three separate** requests, all rejected with 400.

Worse, the expansion also split the arguments of the `expect` helper. It ended up comparing the output of two of those failed requests with each other, found them equal, and printed ✔ for "admin credits ₦10,000". The only visible symptom was the *next* check: a transfer reported `insufficient_funds`.

- **How it was caught:** the server's request log showed several 400s for what should have been a single credit, and `bash -x` showed the split arguments.
- **Fix:** every JSON body in the script is now built with `jq`.
- **Re-check:** all 30 checks pass on both an upgraded database and a fresh `docker compose down -v && up`.

### 6. Things caught in review before they could bite
None of these produced a failure, because they were addressed while writing the code. I list them because each is a plausible AI-generated bug in a ledger:
- **Stale balance after locking.** EF Core returns an *already-tracked* entity unchanged, even after `SELECT … FOR UPDATE` reloads the row. A wallet loaded before being locked would therefore carry a stale balance, silently defeating the lock. `LockWalletAsync` now refuses to lock a wallet that is already tracked.
- **Audit hashes that can't verify.** .NET timestamps have 100 ns precision but PostgreSQL stores microseconds. Hashing the in-memory timestamp would make every audit record fail verification after a round trip. Timestamps are truncated to microseconds (`GetLedgerNow`), and an integration test verifies the chain *after* reading it back from the database.
- **Retry policy.** `EnableRetryOnFailure`, a commonly suggested EF setting, is incompatible with explicit transactions, so it was deliberately left out.
- **Config read too early.** The first drafts read the connection string and rate limits while registering services. With minimal hosting, that can ignore configuration added later by a test host, so both are now resolved lazily.
- **Leaking the recipient's balance.** The obvious receipt shape returns both balances after a transfer, which would tell a sender the recipient's balance. The receipt returns only the caller's side.
- **Tokens minted on a fake clock.** The daily-limit test runs the API on a fake clock set to a fixed time. After the switch to real sign-in, tokens issued by that host would have carried the fake time and been rejected by real-time validation. The test signs in through the real-clock host instead.
- **Revocation that only happens at expiry.** A JWT stays valid until it expires, so "disable user" would take up to 15 minutes to bite. Each request now checks the user's status, role and `token_version`, and disabling or re-roling a user bumps that version.
- **Two admins demoting each other.** Checking "is there another admin?" without a lock lets two concurrent demotions leave nobody in charge. The check locks all active admin rows first.

### 7. What "naive" looks like, measured
The classic generated transfer is: read the balance, check it, update it, all without a lock. To show why that is unacceptable here, the agent removed `FOR UPDATE` and re-ran the concurrency suite. It did the same later for the two safeguards in the auth code:

| Mutation | Outcome |
|---|---|
| No row lock | **199 of 200** ₦100 transfers "succeeded" from a ₦1,000 wallet (lost updates); all 40 transfers passed a ₦500k daily limit that should have stopped 15; 4 of 5 concurrency tests failed |
| No lock ordering | PostgreSQL `40P01 deadlock detected` under opposing transfers |
| No per-request user check on tokens | Disabled and demoted users kept access, and a token claiming a role its user doesn't have was accepted; 4 tests failed |
| No row lock on refresh-token rotation | **10 of 10** concurrent refreshes of one token succeeded, so a stolen refresh token could be cloned |

Everything was restored, and the full suite (148 tests) passes again.

## What I took away
- AI was fastest at boilerplate (EF mappings, Problem Details plumbing, Swagger, Dockerfile, CI) and at producing a broad first test list.
- It needed direction on the parts that matter most in a ledger: lock scope and ordering, what goes in the same transaction, what to cache for idempotency, and what gets logged.
- Tests only earn trust once you have seen them fail for the right reason, hence the deliberate mutations.
