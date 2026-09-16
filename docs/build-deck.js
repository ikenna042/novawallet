const pptxgen = require("pptxgenjs");
const React = require("react");
const ReactDOMServer = require("react-dom/server");
const sharp = require("sharp");
const fa = require("react-icons/fa6");

const OUT = process.argv[2] || "deck.pptx";

const C = {
  navy: "0B1F3A", navy2: "163A63", gold: "D4A63A", goldSoft: "F6ECD3",
  ink: "1B2430", muted: "5B6675", card: "F3F5F9", line: "D5DBE5",
  white: "FFFFFF", green: "2E8B57", greenSoft: "E3F2EA", red: "B83A2E", redSoft: "F8E4E1",
};
const HEAD = "Cambria", BODY = "Calibri", MONO = "Courier New";

async function icon(Comp, color, size = 256) {
  const svg = ReactDOMServer.renderToStaticMarkup(React.createElement(Comp, { color: "#" + color, size: String(size) }));
  const buf = await sharp(Buffer.from(svg)).png().toBuffer();
  return "image/png;base64," + buf.toString("base64");
}

(async () => {
  const pres = new pptxgen();
  pres.layout = "LAYOUT_16x9"; // 10 x 5.625
  pres.author = "Ikenna Odoh";
  pres.title = "NovaWallet Ledger Service — Ikenna Odoh";

  const I = {};
  const need = {
    wallet: fa.FaWallet, lock: fa.FaLock, key: fa.FaKey, clock: fa.FaClock, scroll: fa.FaScroll,
    shield: fa.FaShieldHalved, bolt: fa.FaBolt, db: fa.FaDatabase, code: fa.FaCode, check: fa.FaCircleCheck,
    x: fa.FaCircleXmark, robot: fa.FaRobot, bug: fa.FaBug, flask: fa.FaFlask, docker: fa.FaDocker,
    naira: fa.FaNairaSign, user: fa.FaUserShield, phone: fa.FaMobileScreen, file: fa.FaFileShield,
    gauge: fa.FaGaugeHigh, arrows: fa.FaArrowRightArrowLeft, list: fa.FaListCheck, eye: fa.FaEye,
    envelope: fa.FaEnvelopeOpenText, route: fa.FaRoute, layers: fa.FaLayerGroup, triangle: fa.FaTriangleExclamation,
  };
  for (const [k, v] of Object.entries(need)) {
    I[k] = await icon(v, C.white);
    I[k + "Navy"] = await icon(v, C.navy);
  }

  const shadow = () => ({ type: "outer", color: "000000", blur: 6, offset: 1.5, angle: 90, opacity: 0.12 });

  const text = (s, t, o) => s.addText(t, { isTextBox: true, fontFace: BODY, color: C.ink, margin: 0, valign: "top", ...o });

  const header = (s, kicker, title) => {
    text(s, kicker.toUpperCase(), { x: 0.5, y: 0.3, w: 9, h: 0.25, fontSize: 10, bold: true, color: C.gold, charSpacing: 2 });
    text(s, title, { x: 0.5, y: 0.55, w: 9, h: 0.6, fontFace: HEAD, fontSize: 24, bold: true, color: C.navy, valign: "middle" });
  };
  const footer = (s, n) => {
    text(s, "NovaWallet Ledger Service  ·  Ikenna Odoh", { x: 0.5, y: 5.28, w: 6, h: 0.2, fontSize: 8, color: C.muted });
    text(s, String(n), { x: 8.5, y: 5.28, w: 1, h: 0.2, fontSize: 8, color: C.muted, align: "right" });
  };
  const bubble = (s, img, x, y, d, fill = C.navy) => {
    s.addShape(pres.shapes.OVAL, { x, y, w: d, h: d, fill: { color: fill }, line: { color: fill } });
    const p = d * 0.26;
    s.addImage({ data: img, x: x + p, y: y + p, w: d - 2 * p, h: d - 2 * p });
  };
  const card = (s, x, y, w, h, fill = C.card, withShadow = false) =>
    s.addShape(pres.shapes.ROUNDED_RECTANGLE, {
      x, y, w, h, rectRadius: 0.08, fill: { color: fill }, line: { color: fill }, ...(withShadow ? { shadow: shadow() } : {}),
    });

  // Bullet list drawn with explicit dots so it renders identically everywhere.
  const dotList = (s, items, o) => {
    const size = o.fontSize || 12, gap = o.gap ?? 0.12;
    const cpl = Math.max(1, ((o.w - 0.22) * 72) / (size * 0.5));
    let y = o.y;
    for (const item of items) {
      const lines = Math.ceil(item.length / cpl);
      const h = (lines * size * 1.22) / 72;
      s.addShape(pres.shapes.OVAL, { x: o.x, y: y + (size * 0.62) / 72 - 0.035, w: 0.07, h: 0.07,
        fill: { color: o.dot || C.gold }, line: { color: o.dot || C.gold } });
      text(s, item, { x: o.x + 0.2, y, w: o.w - 0.2, h, fontSize: size, color: o.color || C.ink });
      y += h + gap;
    }
  };

  // ---------- 1. Title ----------
  {
    const s = pres.addSlide();
    s.background = { color: C.navy };
    bubble(s, I.naira, 0.7, 0.8, 0.9, C.gold);
    text(s, "CASE STUDY  ·  BACKEND ENGINEER (.NET / C#)", { x: 0.7, y: 1.95, w: 8.5, h: 0.3, fontSize: 12, bold: true, color: C.gold, charSpacing: 2 });
    text(s, "NovaWallet Ledger Service", { x: 0.7, y: 2.3, w: 8.6, h: 0.9, fontFace: HEAD, fontSize: 44, bold: true, color: C.white });
    text(s, "A wallet ledger that never loses, duplicates or miscounts a customer's naira — and can prove it under load.",
      { x: 0.7, y: 3.25, w: 7.6, h: 0.7, fontSize: 16, color: "C9D3E3" });
    text(s, [
      { text: "Ikenna Odoh", options: { bold: true, color: C.white, breakLine: true } },
      { text: "FirstBank Digital Factory  ·  IT-CIO Organization  ·  22 September 2026", options: { color: "9FB0C8" } },
    ], { x: 0.7, y: 4.35, w: 8, h: 0.6, fontSize: 13 });
    text(s, ".NET 8  ·  PostgreSQL 16  ·  Docker Compose", { x: 5.8, y: 0.95, w: 3.6, h: 0.3, fontSize: 11, color: "9FB0C8", align: "right" });
    s.addNotes("Good morning. I'll walk you through the NovaWallet ledger I built for the take-home: what the brief demanded, how the design guarantees correctness under concurrency, the evidence that it works, where AI helped and where it was wrong, and the trade-offs I'd revisit. About ten minutes, then I'm happy to change code live.");
  }

  // ---------- 2. Brief ----------
  {
    const s = pres.addSlide();
    s.background = { color: C.white };
    header(s, "The brief", "Six capabilities, four hard constraints");
    const caps = [
      [I.wallet, "Create wallet", "per customer, starts at ₦0"],
      [I.naira, "Balance & credit", "kobo, NGN; simulated inbound NIP"],
      [I.arrows, "Atomic transfer", "concurrency-safe"],
      [I.key, "Idempotency-Key", "replay ≠ double-process"],
      [I.list, "Statement", "paginated, newest first"],
      [I.clock, "Limit + audit", "₦500k/day, WAT midnight; append-only"],
    ];
    caps.forEach(([img, t, d], i) => {
      const col = i % 2, row = Math.floor(i / 2);
      const x = 0.5 + col * 2.75, y = 1.45 + row * 1.2;
      card(s, x, y, 2.55, 1.0);
      bubble(s, img, x + 0.15, y + 0.22, 0.56);
      text(s, t, { x: x + 0.85, y: y + 0.18, w: 1.6, h: 0.3, fontSize: 13, bold: true, color: C.navy });
      text(s, d, { x: x + 0.85, y: y + 0.48, w: 1.62, h: 0.45, fontSize: 10.5, color: C.muted });
    });
    card(s, 6.2, 1.45, 3.3, 3.4, C.navy);
    text(s, "NON-NEGOTIABLE", { x: 6.45, y: 1.62, w: 2.9, h: 0.25, fontSize: 10, bold: true, color: C.gold, charSpacing: 2 });
    const hard = [
      "Integers in kobo — no float/double in the money path",
      "Balance never negative under any interleaving",
      "JWT bearer auth + RFC 7807 errors",
      "docker compose up — the panel runs it",
    ];
    hard.forEach((h, i) => {
      const y = 2.0 + i * 0.7;
      s.addImage({ data: I.check, x: 6.45, y: y + 0.03, w: 0.24, h: 0.24 });
      text(s, h, { x: 6.82, y, w: 2.5, h: 0.6, fontSize: 12.5, color: C.white });
    });
    footer(s, 2);
    s.addNotes("The brief has six functional capabilities, but the real test is the four hard constraints on the right. Of those, 'balance never negative under ANY interleaving' drove almost every design decision, followed by idempotency, because mobile and USSD clients on Nigerian networks retry a lot.");
  }

  // ---------- 3. Results at a glance ----------
  {
    const s = pres.addSlide();
    s.background = { color: C.white };
    header(s, "Outcome", "What was delivered — and verified");
    const stats = [
      ["200 → 10", "parallel ₦100 transfers from a ₦1,000 wallet: exactly 10 succeed, 190 refused"],
      ["91", "automated tests: 45 unit + 46 against real PostgreSQL, all green"],
      ["0", "negative balances or deadlocks across repeated load runs"],
      ["1", "command to run it: docker compose up (a CI job runs exactly that)"],
    ];
    stats.forEach(([big, label], i) => {
      const x = 0.5 + i * 2.3;
      card(s, x, 1.5, 2.1, 2.25, C.card, true);
      text(s, big, { x: x + 0.15, y: 1.7, w: 1.8, h: 0.8, fontFace: HEAD, fontSize: i === 0 ? 30 : 40, bold: true, color: C.navy, valign: "middle" });
      text(s, label, { x: x + 0.15, y: 2.6, w: 1.8, h: 1.05, fontSize: 11.5, color: C.muted });
    });
    text(s, [
      { text: "Also shipped: ", options: { bold: true, color: C.navy } },
      { text: "Swagger/OpenAPI · rate limiting · transactional outbox (TransferCompleted) · Serilog correlation IDs · health/readiness probes · hash-chained audit trail · GitHub Actions CI with a compose smoke test · README + AI_USAGE.md" },
    ], { x: 0.5, y: 4.1, w: 9, h: 0.8, fontSize: 12.5, color: C.ink });
    footer(s, 3);
    s.addNotes("The headline: 200 transfers fired at the same instant against a thousand-naira wallet — exactly ten succeed, every time, balance lands on zero, and the ledger agrees. 91 tests in total, 46 of them against real Postgres. All four stretch goals are in as well.");
  }

  // ---------- 4. Architecture ----------
  {
    const s = pres.addSlide();
    s.background = { color: C.white };
    header(s, "Architecture", "Clean layers; the database is the last guard");
    const layers = [
      ["NovaWallet.Api", "Controllers · JWT · Problem Details · rate limiter · correlation ID · Swagger · health", I.code],
      ["NovaWallet.Application", "TransferService · WalletService · DailyLimitPolicy · ILedgerStore port", I.layers],
      ["NovaWallet.Infrastructure", "EF Core + raw SQL row locks · migrations · outbox worker", I.db],
    ];
    layers.forEach(([t, d, img], i) => {
      const y = 1.45 + i * 1.05;
      card(s, 0.5, y, 5.3, 0.85, i === 1 ? C.goldSoft : C.card);
      bubble(s, img, 0.65, y + 0.15, 0.55);
      text(s, t, { x: 1.35, y: y + 0.1, w: 4.3, h: 0.3, fontSize: 14, bold: true, color: C.navy });
      text(s, d, { x: 1.35, y: y + 0.42, w: 4.35, h: 0.4, fontSize: 10.5, color: C.muted });
      if (i < 2) s.addShape(pres.shapes.DOWN_ARROW, { x: 3.0, y: y + 0.87, w: 0.3, h: 0.16, fill: { color: C.gold }, line: { color: C.gold } });
    });
    // Domain side panel
    card(s, 0.5, 4.6, 5.3, 0.5, C.navy);
    text(s, [
      { text: "NovaWallet.Domain  ", options: { bold: true, color: C.gold } },
      { text: "Money (long kobo, checked) · Wallet · LedgerEntry · AuditRecord", options: { color: C.white } },
    ], { x: 0.7, y: 4.6, w: 5.0, h: 0.5, fontSize: 11, valign: "middle" });
    // Postgres panel
    card(s, 6.2, 1.45, 3.3, 3.65, C.navy);
    bubble(s, I.dbNavy, 6.4, 1.6, 0.55, C.gold);
    text(s, "PostgreSQL 16", { x: 7.1, y: 1.72, w: 2.3, h: 0.35, fontSize: 15, bold: true, color: C.white });
    const guards = [
      "CHECK balance_kobo ≥ 0",
      "Append-only triggers on ledger + audit",
      "Unique (customer, idempotency key)",
      "Unique NIP reference",
      "Outbox in the same transaction",
    ];
    dotList(s, guards, { x: 6.4, y: 2.35, w: 2.95, fontSize: 12, color: C.white });
    footer(s, 4);
    s.addNotes("Four projects. Domain has no dependencies — Money is a long of kobo with checked arithmetic. Application owns every business rule and talks to storage through one port, so the orchestration is unit-testable. Infrastructure has the locking SQL. The API is transport only. On the right: even if the application had a bug, the database refuses a negative balance or an edit to history.");
  }

  // ---------- 5. Transfer flow ----------
  {
    const s = pres.addSlide();
    s.background = { color: C.white };
    header(s, "Core algorithm", "One transfer = one database transaction");
    const steps = [
      ["1", "Claim key", "INSERT … ON CONFLICT DO NOTHING — a duplicate in flight waits here"],
      ["2", "Lock wallets", "SELECT … FOR UPDATE, lower id first → no deadlocks"],
      ["3", "Check rules", "owner · funds · daily limit — all while holding the locks"],
      ["4", "Write all", "balances · transaction · 2 entries · 2 audit rows · outbox"],
      ["5", "Commit", "store response on the key; locks released"],
    ];
    steps.forEach(([n, t, d], i) => {
      const x = 0.5 + i * 1.84;
      card(s, x, 1.45, 1.66, 2.05, C.card);
      s.addShape(pres.shapes.OVAL, { x: x + 0.15, y: 1.6, w: 0.5, h: 0.5, fill: { color: i === 1 ? C.gold : C.navy }, line: { color: i === 1 ? C.gold : C.navy } });
      text(s, n, { x: x + 0.15, y: 1.6, w: 0.5, h: 0.5, fontSize: 16, bold: true, color: C.white, align: "center", valign: "middle" });
      text(s, t, { x: x + 0.15, y: 2.2, w: 1.4, h: 0.3, fontSize: 13, bold: true, color: C.navy });
      text(s, d, { x: x + 0.15, y: 2.52, w: 1.42, h: 0.95, fontSize: 10, color: C.muted });
    });
    card(s, 0.5, 3.7, 5.6, 1.4, C.navy);
    text(s, [
      "// lock in a fixed order, then decide",
      "var (first, second) = src < dst ? (src, dst) : (dst, src);",
      "await store.LockWalletAsync(first);   // FOR UPDATE",
      "await store.LockWalletAsync(second);",
      "dailyLimit.EnsureWithinLimit(spentToday, amount);",
      "source.Debit(amount); destination.Credit(amount);",
    ].join("\n"), { x: 0.7, y: 3.8, w: 5.3, h: 1.2, fontFace: MONO, fontSize: 9.5, color: "E6EDF7", valign: "middle" });
    card(s, 6.3, 3.7, 3.2, 1.4, C.redSoft);
    text(s, [
      { text: "On a business rejection", options: { bold: true, color: C.red, breakLine: true } },
      { text: "discard writes, store the error against the key, commit. On an unexpected failure: roll back everything, key included — safe to retry.", options: { color: C.ink } },
    ], { x: 6.45, y: 3.8, w: 2.95, h: 1.25, fontSize: 11 });
    footer(s, 5);
    s.addNotes("This is the heart of it. Everything is one transaction at read committed. Step one claims the idempotency key. Step two takes row locks on both wallets — always the lower id first, so an A-to-B and a B-to-A can't deadlock. Only then do I read the balance and today's spend, so no other request can change them underneath me. Why pessimistic locks rather than optimistic versioning or SERIALIZABLE? Hot wallets under contention would just retry-storm, and a conditional UPDATE can't cover the daily-limit sum. Row locks are simple to reason about and to defend.");
  }

  // ---------- 6. Idempotency ----------
  {
    const s = pres.addSlide();
    s.background = { color: C.white };
    header(s, "Idempotency", "Retries are normal on USSD — make them harmless");
    const rows = [
      ["Same key, same body", "Original receipt, 201, Idempotent-Replayed: true", C.greenSoft, I.checkNavy],
      ["Same key, different body", "422 idempotency_key_reused", C.redSoft, I.xNavy],
      ["Same key, first attempt was declined", "Same error again (cached) — even after a top-up", C.goldSoft, I.scrollNavy],
      ["Two requests, same key, same instant", "Second waits on the unique index, then replays", C.card, I.boltNavy],
      ["Server crashed mid-request", "Transaction rolled back, key released — retry is safe", C.card, I.shieldNavy],
    ];
    rows.forEach(([a, b, fill, img], i) => {
      const y = 1.45 + i * 0.62;
      card(s, 0.5, y, 5.9, 0.52, fill);
      s.addImage({ data: img, x: 0.65, y: y + 0.13, w: 0.26, h: 0.26 });
      text(s, a, { x: 1.05, y, w: 2.3, h: 0.52, fontSize: 11.5, bold: true, color: C.navy, valign: "middle" });
      text(s, b, { x: 3.4, y, w: 2.9, h: 0.52, fontSize: 11, color: C.ink, valign: "middle" });
    });
    card(s, 6.7, 1.45, 2.8, 3.02, C.navy);
    text(s, "DESIGN", { x: 6.9, y: 1.6, w: 2.4, h: 0.25, fontSize: 10, bold: true, color: C.gold, charSpacing: 2 });
    const pts = [
      "Key scoped per customer: PK (sub, key)",
      "SHA-256 of canonical request body",
      "Claimed and completed inside the transfer's own transaction",
      "NIP credits: session reference is the key",
    ];
    dotList(s, pts, { x: 6.9, y: 1.95, w: 2.45, fontSize: 11.5, color: C.white });
    text(s, "Test: 50 simultaneous requests with one key → one transfer, 49 identical replays.",
      { x: 0.5, y: 4.62, w: 9, h: 0.35, fontSize: 12, italic: true, color: C.navy });
    footer(s, 6);
    s.addNotes("A customer on *894# whose session times out will retry. So the key is claimed inside the same transaction as the money movement — there is never a moment where money moved but the key isn't recorded, or vice versa. A concurrent duplicate blocks on the primary key and then reads the committed result. I also cache declines, Stripe-style, so the same key always means the same answer.");
  }

  // ---------- 7. Daily limit + audit ----------
  {
    const s = pres.addSlide();
    s.background = { color: C.white };
    header(s, "Controls", "A daily limit and an audit trail you can't edit");
    // left
    card(s, 0.5, 1.45, 4.35, 3.6, C.card);
    bubble(s, I.clock, 0.7, 1.6, 0.55);
    text(s, "₦500,000 / day, resets 00:00 WAT", { x: 1.4, y: 1.7, w: 3.35, h: 0.35, fontSize: 14, bold: true, color: C.navy });
    const l = [
      "Computed from the ledger (single source of truth), under the sender's row lock",
      "WAT = fixed UTC+1 — no tzdata dependency",
      "Injected TimeProvider: tests at 23:59:59 and 00:00 WAT",
      "40 parallel ₦20k transfers → exactly 25 allowed",
    ];
    dotList(s, l, { x: 0.7, y: 2.35, w: 3.95, fontSize: 12 });
    // right
    card(s, 5.15, 1.45, 4.35, 3.6, C.navy);
    bubble(s, I.scrollNavy, 5.35, 1.6, 0.55, C.gold);
    text(s, "Append-only, hash-chained audit_log", { x: 6.05, y: 1.7, w: 3.35, h: 0.35, fontSize: 14, bold: true, color: C.white });
    const r = [
      "One row per balance mutation: before, delta, after, actor, correlation ID",
      "DB triggers reject UPDATE / DELETE / TRUNCATE",
      "Per-wallet SHA-256 chain → GET /audit returns chainIntact",
      "Timestamps truncated to µs so hashes survive the Postgres round trip",
    ];
    dotList(s, r, { x: 5.35, y: 2.35, w: 3.95, fontSize: 12, color: C.white });
    footer(s, 7);
    s.addNotes("The limit is derived from the ledger rather than a separate counter, and it's read while the sender is locked — so parallel transfers can't all squeeze under it. That's tested: 40 parallel twenty-thousand-naira transfers, exactly 25 pass. The audit trail is a separate table the database itself refuses to modify, and each wallet's records are hash-chained, so even someone who disables the trigger leaves evidence.");
  }

  // ---------- 8. Security & context ----------
  {
    const s = pres.addSlide();
    s.background = { color: C.white };
    header(s, "Security & Nigerian context", "Built for where it would actually run");
    const tiles = [
      [I.user, "Auth", "JWT: issuer, audience, lifetime checked; HS256 pinned (alg:none rejected); fallback policy = authenticated; operator role for credits & audit"],
      [I.eye, "No probing", "Other customers' wallets return 404; receipts show only the sender's balance"],
      [I.shield, "Input", "Strict JSON (100.5 or \"100\" refused), unknown fields rejected, safe-charset keys & IDs (no log injection)"],
      [I.file, "NDPA 2023", "No BVN/NIN/names stored — opaque customer ID; request bodies never logged"],
      [I.phone, "USSD & NIP", "Retries made harmless by idempotency; NIP session ID de-duplicates credits; gateway can pass X-Correlation-ID"],
      [I.gauge, "CBN & abuse", "Per-customer rate limit (429 + Retry-After); configurable daily limit; tiered-KYC limits are next"],
    ];
    tiles.forEach(([img, t, d], i) => {
      const col = i % 3, row = Math.floor(i / 3);
      const x = 0.5 + col * 3.05, y = 1.45 + row * 1.85;
      card(s, x, y, 2.85, 1.7, C.card);
      bubble(s, img, x + 0.15, y + 0.15, 0.45);
      text(s, t, { x: x + 0.72, y: y + 0.2, w: 2.0, h: 0.35, fontSize: 13, bold: true, color: C.navy, valign: "middle" });
      text(s, d, { x: x + 0.15, y: y + 0.72, w: 2.58, h: 0.95, fontSize: 10, color: C.ink });
    });
    footer(s, 8);
    s.addNotes("Security: the token check is strict and pinned to one algorithm, and there is a test that an alg-none operator token is refused. Secrets come from the environment; startup fails on a short key. The mock issuer only exists behind a flag. For NDPA, the ledger stores no personal identifiers and never logs bodies. The USSD channel is exactly why idempotency matters, and tiered KYC limits would plug into the same limit policy.");
  }

  // ---------- 9. Evidence ----------
  {
    const s = pres.addSlide();
    s.background = { color: C.white };
    header(s, "Evidence", "The tests fail when the safety is removed");
    s.addChart(pres.charts.BAR, [
      { name: "Succeeded (201)", labels: ["With row lock", "FOR UPDATE removed"], values: [10, 199] },
      { name: "Refused (422)", labels: ["With row lock", "FOR UPDATE removed"], values: [190, 1] },
    ], {
      x: 0.4, y: 1.35, w: 5.3, h: 3.75, barDir: "col", barGrouping: "clustered",
      chartColors: [C.navy, C.gold],
      showTitle: true, title: "200 parallel ₦100 transfers from a ₦1,000 wallet", titleFontSize: 12, titleColor: C.ink, titleFontFace: BODY,
      showValue: true, dataLabelPosition: "outEnd", dataLabelFontSize: 11, dataLabelColor: C.ink,
      showLegend: true, legendPos: "b", legendFontSize: 10,
      catAxisLabelColor: C.muted, catAxisLabelFontSize: 11, valAxisLabelColor: C.muted, valAxisLabelFontSize: 9,
      valGridLine: { color: "E5E9F0", size: 0.5 }, catGridLine: { style: "none" }, valAxisMaxVal: 220,
    });
    const facts = [
      [I.checkNavy, "With the lock", "10 succeed, balance ₦0, ledger sum = balance — identical over 3 runs (~250 ms on an M1)"],
      [I.xNavy, "Lock removed", "199 \"succeed\" from ₦1,000 (lost updates); daily-limit test lets 40/40 through"],
      [I.triangleNavy, "Lock order removed", "PostgreSQL 40P01 deadlock detected in the A⇄B test"],
      [I.flaskNavy, "Also under load", "200 opposing transfers conserve money; 20 senders → 1 wallet all land"],
    ];
    facts.forEach(([img, t, d], i) => {
      const y = 1.45 + i * 0.92;
      s.addImage({ data: img, x: 6.0, y: y + 0.04, w: 0.3, h: 0.3 });
      text(s, t, { x: 6.45, y, w: 3.05, h: 0.3, fontSize: 12.5, bold: true, color: C.navy });
      text(s, d, { x: 6.45, y: y + 0.3, w: 3.05, h: 0.58, fontSize: 10.5, color: C.muted });
    });
    footer(s, 9);
    s.addNotes("A green test suite only means something if it goes red for the right reason. So I deleted FOR UPDATE and re-ran: 199 of 200 transfers 'succeeded' out of a thousand naira — classic lost updates — and four of five concurrency tests failed. Removing the lock ordering produced real Postgres deadlocks. Both were restored and the full suite is green.");
  }

  // ---------- 10. AI usage ----------
  {
    const s = pres.addSlide();
    s.background = { color: C.white };
    header(s, "AI fluency & judgement", "AI wrote fast; checks caught its mistakes");
    card(s, 0.5, 1.45, 2.7, 3.6, C.navy);
    bubble(s, I.robotNavy, 0.7, 1.6, 0.55, C.gold);
    text(s, "How I used it", { x: 0.7, y: 2.3, w: 2.3, h: 0.3, fontSize: 14, bold: true, color: C.white });
    const how = [
      "Plan first, approved before code",
      "Boilerplate: EF mappings, Swagger, Docker, CI",
      "Broad first test list",
      "Nothing accepted without a test or a log to prove it",
    ];
    dotList(s, how, { x: 0.7, y: 2.7, w: 2.35, fontSize: 11, color: C.white });
    const cases = [
      [I.bug, "Errors stopped being Problem Details", "[Produces(\"application/json\")] overrode 400 responses. Caught by a media-type assertion (5 of 7 cases failed). Removed."],
      [I.triangle, "Every declined transfer logged as an ERROR", ".NET 8 exception middleware logs all exceptions at Error — alert fatigue in a bank. Caught reading logs; fixed with an MVC filter → 0 error lines."],
      [I.flask, "Tests that checked the wrong rule", "\"Insufficient funds\" tests used amounts above the daily limit, so the limit fired first. Rewritten to overdraw by exactly 1 kobo."],
    ];
    cases.forEach(([img, t, d], i) => {
      const y = 1.45 + i * 1.22;
      card(s, 3.45, y, 6.05, 1.08, C.card);
      bubble(s, img, 3.6, y + 0.22, 0.5, C.red);
      text(s, t, { x: 4.3, y: y + 0.1, w: 5.1, h: 0.3, fontSize: 12.5, bold: true, color: C.navy });
      text(s, d, { x: 4.3, y: y + 0.42, w: 5.1, h: 0.62, fontSize: 10.5, color: C.ink });
    });
    footer(s, 10);
    s.addNotes("I used Claude Code as the implementer and treated myself as the reviewer. Three real misses: an innocent-looking Produces attribute that broke the Problem Details contract; business declines being logged as server errors, which in a bank means alert fatigue; and tests asserting insufficient funds with amounts that actually tripped the daily limit. Also caught before they bit: EF returning a stale tracked wallet after a lock, and timestamp precision breaking the audit hash chain. All of it is written up in AI_USAGE.md.");
  }

  // ---------- 11. Trade-offs & close ----------
  {
    const s = pres.addSlide();
    s.background = { color: C.navy };
    text(s, "TRADE-OFFS & NEXT STEPS", { x: 0.5, y: 0.35, w: 9, h: 0.25, fontSize: 10, bold: true, color: C.gold, charSpacing: 2 });
    text(s, "What I'd do with another sprint", { x: 0.5, y: 0.62, w: 9, h: 0.6, fontFace: HEAD, fontSize: 26, bold: true, color: C.white, valign: "middle" });
    const tradeoffs = [
      ["Row locks over optimistic retries", "simple, provable; serialises a hot wallet's writes"],
      ["Declines cached per key", "consistent replays; a top-up needs a new key"],
      ["Migrate on startup", "one-command demo; production uses a pipeline job"],
      ["Outbox is at-least-once", "consumers de-duplicate on eventId"],
    ];
    tradeoffs.forEach(([a, b], i) => {
      const y = 1.45 + i * 0.72;
      text(s, a, { x: 0.5, y, w: 4.3, h: 0.3, fontSize: 13, bold: true, color: C.white });
      text(s, b, { x: 0.5, y: y + 0.3, w: 4.3, h: 0.3, fontSize: 11, color: "9FB0C8" });
    });
    const next = [
      "KYC tier limit profiles (Tier 1/2/3)",
      "Key-expiry + nightly reconciliation & chain verification",
      "Reversals as compensating entries",
      "Kafka/Service Bus, OpenTelemetry, least-privilege DB roles",
      "Asymmetric JWT via JWKS; mTLS to NIP settlement",
    ];
    card(s, 5.2, 1.45, 4.3, 2.85, C.navy2);
    dotList(s, next, { x: 5.4, y: 1.6, w: 3.95, fontSize: 12, color: C.white });
    card(s, 5.2, 4.45, 4.3, 0.7, C.gold);
    text(s, [
      { text: "LIVE DEMO", options: { bold: true, color: C.navy, fontSize: 9, charSpacing: 2, breakLine: true } },
      { text: "docker compose up → /swagger → smoke-test.sh", options: { fontFace: MONO, color: C.navy, fontSize: 10.5 } },
    ], { x: 5.4, y: 4.45, w: 4.0, h: 0.7, valign: "middle" });
    text(s, "Thank you — questions, or shall we change some code?", { x: 0.5, y: 4.55, w: 4.5, h: 0.5, fontSize: 14, italic: true, color: C.gold, valign: "middle" });
    s.addNotes("Every choice has a cost: row locks serialise writes on a very hot wallet, cached declines mean a new key after a top-up, and migrate-on-startup is a demo convenience. Next I'd add KYC tier limits, reconciliation and key expiry, reversals as compensating entries, and production-grade eventing and identity. Happy to run it live — compose up, Swagger, and the smoke script — or modify code.");
  }

  await pres.writeFile({ fileName: OUT });
  console.log("wrote", OUT);
})();
