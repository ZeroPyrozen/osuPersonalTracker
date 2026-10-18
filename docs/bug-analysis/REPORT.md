# Bug analysis and fix report: osu!PersonalTracker

Branch `claude/repo-bug-analysis-fixes-mbzyx7`, based on `613e19d` (2026-10-18).
Machine-readable versions: [`bugs.json`](bugs.json) (every field below, plus observations) and
[`bugs.csv`](bugs.csv) (one row per bug).

---

## Executive summary

A full review of the repository found **27 verifiable defects: 1 critical, 4 high, 7 medium
and 15 low. All 27 are fixed.** Twenty-three came from the first pass. Four more (BUG-024 to
BUG-027) began as observations, and a second pass found each had a clear, contained fix.
Every code fix has a test that fails against the original code; BUG-027 is a
documentation fix. The repository now has its first test suite (91 test methods, 123 cases,
all passing) and its first CI pipeline. Fifteen observations (§6) remain written up rather
than changed. They are design questions, need checking against the live osu! API, or sit in
the archived spike.

The findings that matter most:

1. **Anyone could crash the server with one URL (BUG-001, critical).** A `/browse?q=` with
   about 2,000 one-letter words (a 4–5 KB query string) overflowed the stack during
   prerender and killed the process. The app is public behind Tailscale Funnel. Reproduced
   end to end: the server aborted with exit code 134 and refused every request after that.
   Non-finite numbers (`stars>nan`) caused HTTP 500 the same way (BUG-002).
2. **Two paths could mark most of the catalogue "Departed" (BUG-004, BUG-022, high).** The
   catalogue sweep reconciled after `--max-pages` stopped it early, and after a 404 or a
   malformed page. Either way, every map not yet reached left every denominator. A 404 on
   page 2 of a test catalogue departed 4 of its 5 maps.
3. **The first 401 broke token refresh for good (BUG-003, high).** The backfill then swallowed
   the resulting error for every map, so it skipped its entire queue without another API call
   and still reported "Completed" (BUG-010).
4. **Passes were silently lost (BUG-007, medium).** The recent-scores poll asked for failed
   plays too, and osu! returns at most 100 rows. Practice fails pushed real passes out of
   the window, and no other path records them.
5. **The headline numbers drifted (BUG-006, BUG-008, medium).** Attempted and Untouched were
   off by one map for each pass `sync recent` recorded on a map never loaded before, so
   status chips stopped matching their own lists. The 30-day pace also counted accuracy re-plays of maps passed
   long ago.

**What else changed.** There are no schema or migration changes, and no new runtime
dependencies. Behaviour you will notice:

- The sync CLI exits **2** for an unreadable flag or missing settings, and **130** on Ctrl+C.
- A cancelled job shows **Paused** instead of staying "Running".
- A catalogue sweep **fails** on a 404 or a malformed page instead of "completing".
- A backfill **stops** after 10 consecutive failures; `--resume` retries them.
- The recent poll no longer asks for failed plays.
- Rate limits count an IPv6 **/64** as one client, as an IPv4 NAT already was.
- Badges answer proxy revalidations (`W/` tags, tag lists, `*`) with **304**, and carry
  `Content-Security-Policy: default-src 'none'` and `X-Content-Type-Options: nosniff`.
- The README and Sync page now say to run `sync playcounts` daily, not just `sync recent`.

**Existing databases.**

- If you ever ran `sync catalog --max-pages`, or a `sync catalog` run ever logged far fewer
  sets than usual, run `dotnet run -- sync catalog` once. Its upsert restores every map's real status.
- A job left at "Running" by an old cancel resets on its next run.
- Rows dated by the old "best score" rule (BUG-008) age out of the 30-day window on their
  own. Nothing needs re-fetching.

---

## 1. Repository assessment

**Structure.**

| Path | Contents |
|---|---|
| `src/OsuTracker.Web/` | The application: Blazor Server (interactive), EF Core 10 on SQLite, minimal-API badge endpoints, and a headless `sync` CLI in the same binary |
| `├─ OsuApi/` | osu! API v2 client, client-credentials token provider, shared rate limiter |
| `├─ Sync/` | Catalogue, playcount, score-backfill and recent-scores jobs; CLI dispatch; error sanitiser |
| `├─ Services/` | Progress, browse and badge queries; search parser; pace projection |
| `├─ Badges/` | SVG renderer, PNG rasteriser (SkiaSharp / Svg.Skia), endpoints |
| `├─ Components/` | Razor pages: dashboard, mode, browse, badge, sync, not-found |
| `└─ Data/` | Entities, DbContext, four migrations |
| `spike/OsuTracker.Spike/` | Phase-1 API probes, kept for the record |
| `tests/OsuTracker.Web.Tests/` | **New:** xUnit v3 + bUnit |
| `.github/workflows/ci.yml` | **New:** build, test, dependency audit |

**Stack and dependencies.** .NET 10. EF Core SQLite 10.0.11. SkiaSharp 4.148.0 managed and
native (consistent, verified by rasterising a real PNG). Svg.Skia 5.2.3. NuGet's audit (run
with transitive packages) **found no known vulnerabilities** in the application or the spike.
Patch updates are available (OBS-15).

**Entry points and boundaries.**

- *Web host* (`Program.cs`): forwarded headers trusted from loopback only, rate limiting on
  the badges and on `/_blazor/negotiate`, migrations at startup, WAL mode.
- *Headless CLI* (`dotnet run -- sync <command>`): runs one job and exits.
- *External boundaries:* osu! API v2 (outbound, rate-limited to 60 requests per minute);
  anonymous public visitors (inbound, through Tailscale Funnel); the SQLite file.

**Build and CI.** The solution built with 0 warnings. **There was no CI and no tests.**

**Documentation.** The README is unusually precise about the counting rules. It now also
says that recent polling is capped at 100 passes per mode and how to run the tests, and it
no longer says `sync recent` alone keeps the tracker current (BUG-027).

---

## 2. How the bugs were found

- **Line-by-line review** of every C#, Razor and JavaScript file in the application and the
  spike, and of every hand-written migration.
- **Empirical verification before any fix.** A scratch harness exercised each suspected
  defect against the real classes. The two most serious were reproduced against the running
  server: the process abort, and HTTP 500 for `stars>nan`.
- **Contract checks against osu-web's source**, because osu.ppy.sh is unreachable from the
  analysis sandbox: `UsersController::scores` and `parsePaginationParams` (`MAX_RESULTS = 100`),
  `Solo\Score::scopeRecent` and `scopeIncludeFails`, and `BeatmapsController::userScoreAll`
  (returns `{"scores": []}` with HTTP 200 when there are no scores). That last check showed
  the backfill's `/all` → best-score fallback was **not** a bug.
- **Disproved hypotheses were dropped, not reported.** ISO-8601 date *parsing* turned out to
  be culture-safe in every culture tested; only *formatting* was not (BUG-013). One race
  scenario on the mode page also proved benign (§5).
- **Static analysis** at the default level and with `AnalysisMode=All`.
- **Dependency audit** with NuGet's vulnerability data, transitive packages included.
- **Benchmarks** on a synthetic 215,000-map catalogue for the one fix that changes a hot query.

---

## 3. Findings, by priority

| ID | Severity | Category | Component | Finding | Fix |
|---|---|---|---|---|---|
| BUG-001 | **Critical** | Security / DoS | Browse search | One anonymous GET with ~2,000 search words crashes the server | `445b3a6` |
| BUG-002 | High | Security / input validation | Browse search | `stars>nan`, `bpm=infinity`, `ar<1e999` → SQL error, HTTP 500 | `445b3a6` |
| BUG-003 | High | Error recovery | Token provider | After any 401 the provider throws forever | `4b6cdf4` |
| BUG-004 | High | Data integrity | Catalogue sync | `--max-pages` reconciles a partial sweep → maps marked Departed | `0ff2429` |
| BUG-022 | High | Data integrity | Catalogue sync | A 404 or malformed page is taken as the end of the catalogue and reconciled | `3837077` |
| BUG-005 | Medium | State / cancellation | All sync jobs, CLI | Ctrl+C leaves jobs "Running" forever; Paused never recorded | `0ff2429`, `d3c2816` |
| BUG-006 | Medium | Logic error | Progress, bands, badge, chips | Attempted/Untouched miscounted; chips disagree with their lists | `d93b7b3`, `4a601a1` |
| BUG-007 | Medium | Integration / data loss | Recent poll | `include_fails=1` lets fails push passes out of the 100-row window | `ba1775f` |
| BUG-008 | Medium | Metrics | Recent poll, backfill | Pace and "+N in 30d" inflated by accuracy re-plays | `ba1775f` |
| BUG-010 | Medium | Resilience | Score backfill | Systemic failures skipped map by map; job reports Completed | `0ff2429` |
| BUG-014 | Medium | Config validation | Sync CLI | Missing UserId → every sync "completes" having fetched nothing | `d3c2816` |
| BUG-015 | Medium | Input validation | Sync CLI | Unreadable `--mode`/`--limit` ignored (`--mode ctb` backfills everything) | `d3c2816` |
| BUG-009 | Low | Data consistency | Recent poll | An upgraded row keeps the old IsLazer flag | `ba1775f` |
| BUG-011 | Low | Retry policy | API client | 400/403/422 retried five times with ~30 s of backoff | `4b6cdf4` |
| BUG-012 | Low | HTTP caching | Badge | Strong ETag and PNG cache key ignore the drawn UPDATED date | `0a0bbf8` |
| BUG-013 | Low | i18n | Badge, Browse | Dates printed in the server culture's calendar (th-TH → 2569) | `0a0bbf8` |
| BUG-016 | Low | State | Catalogue sync | `--resume` on a completed sweep double-counts | `0ff2429` |
| BUG-017 | Low | Integer overflow | Browse paging | Page ≥ 42,949,673 wraps negative and serves page one | `445b3a6` |
| BUG-018 | Low | Input validation | Browse search | `ranked=2019-13` silently means all of 2019 | `445b3a6` |
| BUG-019 | Low | Rate limiting | Rate limiter | `PenaliseAsync` does not drain the bucket as documented | `4b6cdf4` |
| BUG-020 | Low | UI / config | Sync page | Budget panel hard-codes "/ 60" | `f06471d` |
| BUG-021 | Low | Concurrency / UI | Browse, mode page | An older load can overwrite a newer view | `f06471d` |
| BUG-023 | Low | JSON robustness | Backfill, recent, playcounts | Shape guards throw instead of skipping | `3837077` |
| BUG-024 | Low | Security / rate limiting | Rate limiters | IPv6 clients get a fresh budget from every address in their /64 | `a627d3a` |
| BUG-025 | Low | HTTP caching | Badge | `If-None-Match` compared verbatim: `W/` tags, lists and `*` never revalidate | `f00c7c1` |
| BUG-026 | Low | Security hardening | Badge | Badge responses carry no CSP and no `nosniff` | `f00c7c1` |
| BUG-027 | Low | Documentation | README, Sync page | "`sync recent` is all you need" leaves maps played without passing counted Untouched | `286dc0c` |

BUG-024 to BUG-027 were promoted from OBS-08, OBS-11, OBS-09 (its badge part) and OBS-18.

---

## 4. Findings in detail

Line numbers refer to the original code at `613e19d`.

### Critical

#### BUG-001 · One anonymous GET crashes the whole server
*Where:* `Services/BrowseSearch.cs:46`, `Services/BrowseQueryService.cs:97` · *Fixed in* `445b3a6`

- **Behaviour.** Every search term becomes another nested `Where()`, and EF Core translates
  that nesting recursively. On a thread-pool thread (where requests run), 2,000 one-letter
  words overflowed the stack. Around 1,000 words already failed with SQLite's
  `Expression tree is too large (maximum depth 1000)`. Browse prerenders, so a plain GET is enough.
- **Reproduce.**
  `curl "http://host/browse?q=$(python3 -c "print('+'.join(['a']*2500))")"` → the connection
  drops, the log ends with `Stack overflow.`, the process exits 134, and nothing answers after that.
- **Root cause.** Unbounded public input fed into a recursively translated expression tree.
- **Impact.** A remote, unauthenticated, repeatable crash of the whole server. An
  uncatchable `StackOverflowException` cannot be handled in-process.
- **Fix.** `BrowseSearch.Parse` keeps at most `MaxTerms = 16` terms and `MaxLength = 256`
  characters. It never splits a surrogate pair, and the search box carries the same
  `maxlength`. The parser's own "parsing is total" rule still holds: an oversized search is
  cut short, not refused.
- **Verified by.** `A_flood_of_words_is_cut_to_a_bounded_number_of_terms`,
  `The_bound_counts_filters_as_well_as_words`,
  `An_oversized_search_is_truncated_before_it_becomes_a_pattern`,
  `A_search_past_sqlites_expression_depth_still_answers`, and
  `Thousands_of_terms_on_a_request_thread_do_not_take_the_process_down`. The last one runs
  5,000 terms on a thread-pool thread. The smoke test (§5) repeats the crash payload against
  the fixed server.

### High

#### BUG-002 · Non-finite numbers in a search fail the page with a SQL error
*Where:* `Services/BrowseSearch.cs:135`, `Services/BrowseQueryService.cs:186` · *Fixed in* `445b3a6`

- **Behaviour.** `double.TryParse` accepts `NaN` and `Infinity`, and `1e999` overflows to
  Infinity. The value is inlined into the SQL as a literal, and SQLite reads it as a column
  name: `no such column: NaN`.
- **Impact.** HTTP 500 on prerender and a dead circuit interactively. The link itself is
  poisoned, so anyone who opens it gets the error.
- **Fix.** Numbers must be `double.IsFinite`. Anything else stays text, as the parser promises.
  Lengths built from m:ss parts get the same check.
- **Verified by.** `Non_finite_numbers_are_searched_as_text` (5 cases) and
  `Non_finite_numbers_answer_instead_of_failing_the_page` (3 cases).

#### BUG-003 · After any 401 the token provider throws forever
*Where:* `OsuApi/OsuTokenProvider.cs:22`, `:28`, `:57` · *Fixed in* `4b6cdf4`

- **Behaviour.** Freshness was tested as `UtcNow < _expiresAt.AddMinutes(-1)`.
  `Invalidate()` sets `_expiresAt = DateTimeOffset.MinValue`, and MinValue minus a minute is
  unrepresentable. Every `GetTokenAsync` after the first 401 therefore threw
  `ArgumentOutOfRangeException`, including the retry that was meant to recover.
- **Impact.** The 401 branch in `OsuApiClient` could never work. During a backfill, combined
  with BUG-010, every remaining map failed at once and was skipped at rate-limit speed, and
  the job ended "Completed".
- **Fix.** The minute is added to "now" instead of subtracted from the expiry:
  `UtcNow.AddMinutes(1) < _expiresAt`.
- **Verified by.** `A_token_can_be_fetched_again_after_it_is_invalidated` and
  `A_401_mid_sweep_is_recovered_with_a_fresh_token`. Behaviour pins: one token request
  shared by 16 concurrent callers, and a cached token reused.

#### BUG-004 · `--max-pages` reconciles a partial sweep
*Where:* `Sync/CatalogSyncJob.cs:117`, `:132` · *Fixed in* `0ff2429`

- **Behaviour.** Stopping early fell through to `ReconcileAsync`. That marks every counted
  map not seen in this run as Departed. The job was then marked Completed and its cursor
  cleared, contradicting the code's own comment ("Running this on a partial run would mark
  most of the catalogue as departed").
- **Reproduce.** On a seeded catalogue, `sync catalog --max-pages 1` departed everything
  beyond page 1.
- **Fix.** Stopping early now pauses the job, keeps its cursor and does not reconcile.
  `--resume` continues from the same page.
- **Verified by.** `Stopping_early_with_max_pages_leaves_the_rest_of_the_catalogue_counted`
  and `A_sweep_stopped_by_max_pages_resumes_where_it_stopped_and_then_completes`.

#### BUG-022 · A 404 or malformed page is taken as the end of the catalogue
*Where:* `Sync/CatalogSyncJob.cs:85`, `:88` · *Fixed in* `3837077`

- **Behaviour.** The sweep `break`s on any of three anomalies, then reconciles:
  `doc is null` (a 404), a page without a `beatmapsets` array, or a non-object body. (A
  non-object body actually threw from `TryGetProperty`, which is what saved it.)
- **Reproduce.** A 404 or `{}` on page 2 of a 5-set catalogue → **4 of 5 maps Departed**, and
  the job Completed. On page one, for example if the endpoint ever moved, every denominator
  would be emptied.
- **Fix.** The catalogue ends only at a null cursor or an empty page. An empty page is a
  real ending when the total is an exact multiple of the page size. Anything else fails the
  run with its cursor kept, reported through the existing `SyncError` texts: "osu! API
  returned 404" or "unexpected response shape from the osu! API".
- **Verified by.** `A_page_that_is_not_a_page_fails_the_run_instead_of_ending_the_catalogue`
  (4 cases) and `An_empty_page_still_ends_the_sweep_normally`.

### Medium

#### BUG-005 · Ctrl+C leaves jobs "Running" forever
*Where:* `Sync/CatalogSyncJob.cs:125`, `:141`; `PlayCountSyncJob.cs:106`; `ScoreBackfillJob.cs:130`; `RecentScoresJob.cs:67` · *Fixed in* `0ff2429`, `d3c2816`

- **Behaviour.** Each job wrote its final state with the run's own token. After Ctrl+C that
  token is cancelled, so the write threw before saving. The stack trace shows
  `RelationalConnection.OpenInternalAsync`. The cancel nearly always lands mid-request, in
  the catch-all, which then also tried to write "Failed" with the same dead token. The CLI
  finally died with an unhandled `OperationCanceledException`.
- **Impact.** The nav badge said "syncing" on every page and `/sync` showed a phantom
  running job. The `Paused` state was unreachable.
- **Fix.** `MarkAsync` takes no token, because it is the write that records the cancel.
  Each job treats `OperationCanceledException` on its own token as a pause, and the CLI
  exits 130 with one line.
- **Verified by.** A cancel-mid-request test for each of the four jobs, plus
  `Ctrl_c_exits_130_with_the_job_paused_rather_than_a_stack_trace`.

#### BUG-006 · Attempted/Untouched miscounted; chips disagree with their lists
*Where:* `Services/ProgressQueryService.cs:72`, `:119`; `Services/BrowseQueryService.cs:222` · *Fixed in* `d93b7b3`, `4a601a1`

- **Behaviour.** Attempted was computed as *played − passed*. That assumes every pass has a
  playcount row, but a pass recorded by `sync recent` on a map never loaded before has none
  until the next `sync playcounts`. The README said that once backfill is done, `sync recent`
  is all you need (corrected as BUG-027); the Sync page suggests running playcounts daily.
- **Reproduce.** One such pass gives a ladder of (total 5, passed 2, attempted **1**,
  untouched **2**), where the truth is (5, 2, 2, 1). The Untouched chip said 2 and opened a
  list of 1. On the synthetic 215k-map catalogue, 29 catch maps were misplaced.
- **Impact.** The dashboard, mode pages, star bands, badge and chips were all wrong. Following
  the README, the error grew every day. Even on the Sync page's daily schedule, every such pass
  since the last playcount sync was miscounted. It also broke the README's promise that
  every screen agrees.
- **Fix.** `ProgressQueryService.Attempted()` defines the rung once, in the same terms as
  Browse's per-row filter: a playcount above zero and no counting pass. The ladder, bands
  and chips all count it directly. The pass check is correlated on the playcount, so SQLite
  probes only played maps (§5, performance).
- **Verified by.** The ladder and band tests, and `Each_status_chip_counts_exactly_the_rows_its_filter_shows`,
  which checks every chip against its own list.

#### BUG-007 · Failed plays push passes out of the 100-row poll window
*Where:* `Sync/RecentScoresJob.cs:39` · *Fixed in* `ba1775f`

- **Behaviour.** osu-web returns at most the newest 100 rows of `/users/{id}/scores/recent`,
  however it is paged, because `parsePaginationParams` caps offset plus limit at
  `MAX_RESULTS = 100`. With `include_fails=1`, failed plays took their share of those rows.
  The job then threw them away; its comment said it kept them "so we can count the play",
  but nothing ever counted them.
- **Impact.** Passes were lost permanently. The backfill never revisits a map, and this poll
  is the only path that records a new pass.
- **Fix.** The poll asks for passes only. The `passed` flag is still checked defensively, and
  the README documents the 100-pass-per-mode window.
- **Verified by.** `A_pass_behind_a_hundred_newer_fails_is_still_recorded`, against a fake
  that implements osu-web's paging and `include_fails` semantics.

#### BUG-008 · Pace inflated by accuracy re-plays of old passes
*Where:* `Sync/RecentScoresJob.cs:147`; `Sync/ScoreBackfillJob.cs:201` · *Fixed in* `ba1775f`

- **Behaviour.** `PlayedAtUnix` drives "+N passed in the last 30 days" and the projected
  finish. Two things set it wrongly. The poll re-dated a counted pass to *today* whenever a
  better run arrived. The backfill dated each map by its *best-accuracy* score rather than
  by when it was first cleared.
- **Fix.** A row is dated by its earliest qualifying pass, and better runs replace only what
  the row shows. A promotion from pre-rank, or an earlier run, still moves the date.
- **Verified by.** Five tests covering re-plays, promotion, two passes in one window,
  backfill dating, and pre-rank versus post-rank qualification.

#### BUG-010 · Systemic failures are skipped map by map
*Where:* `Sync/ScoreBackfillJob.cs:70` · *Fixed in* `0ff2429`

- **Behaviour.** Every per-map exception was skipped and the cursor advanced. That is right
  for one bad map and wrong for revoked credentials, an outage, or BUG-003.
- **Reproduce.** The token endpoint returns 401 → all 40 queued maps are attempted and the
  job reports "Completed".
- **Fix.** Ten failures in a row fail the run, and only an answered map moves the cursor,
  so `--resume` retries the whole streak.
- **Verified by.** The streak test, the resume-after-streak test, and an isolated-failure pin.

#### BUG-014 · A missing UserId makes every sync "complete" with nothing fetched
*Where:* `Sync/SyncCommands.cs:41` · *Fixed in* `d3c2816`

- **Behaviour.** A UserId set without the `OsuApi:` prefix never binds, and an unbound
  UserId is 0. `/users/0/…` returns 404, which the jobs read as "nothing there" and record
  as Completed. On a timer, that looks exactly like a working sync.
- **Fix.** API commands check their settings first and exit 2 with a pointer to the README.
  Every API command needs ClientId and ClientSecret; playcounts, scores and recent also need
  UserId. `status` still works with no settings at all.

#### BUG-015 · Unreadable CLI flags are silently ignored
*Where:* `Sync/SyncCommands.cs:21`, `:36` · *Fixed in* `d3c2816`

- **Behaviour.** `sync scores --mode ctb` (an alias the badge URLs accept) or `--limit ten`
  fell back to "no restriction" and started an unbounded, multi-hour backfill of every mode.
- **Fix.** A flag that is present must carry a valid value; counts must be positive.
  Otherwise the command exits 2 with a message before anything is fetched.

### Low

| ID | Finding | Root cause → fix | Verified by |
|---|---|---|---|
| BUG-009 | Upgraded recent-score rows kept the old `IsLazer` (`RecentScoresJob.cs:143-155`) | Three hand-copied field blocks had drifted → one `Show()` sets every display field | `An_upgrade_carries_every_display_field_including_the_client` |
| BUG-011 | 400/403/422 retried 5× with 2+4+8+16 s backoff (`OsuApiClient.cs:63`, `:90`) | Transience judged by exception type alone → an arrived status is transient only if ≥ 500 or 408 | `A_client_error_fails_at_once_instead_of_being_retried` (3 cases), plus 5xx and dropped-connection pins |
| BUG-012 | One strong ETag stood for a different SVG each day; the PNG cache, keyed on it, served the first day's date indefinitely (`BadgeService.cs:31`) | The fingerprint lacked the drawn date → `UpdatedOn` is fingerprinted and printed | `The_etag_changes_with_the_date_the_banner_draws`, `…holds_through_the_day…` |
| BUG-013 | `yyyy` counted in the server culture's calendar: th-TH 2569, ar-SA 1448, fa-IR 1405 (`BadgeRenderer.cs:45`, `Browse.razor:158`) | No IFormatProvider → invariant culture | `The_banner_is_the_same_document_whatever_culture_the_server_runs_in` (4 cultures) |
| BUG-016 | `--resume` of a completed sweep doubled ItemsDone (`CatalogSyncJob.cs:56`) | "Resume requested" conflated with "something to resume" | `Resuming_a_sweep_that_already_completed_counts_from_zero` |
| BUG-017 | Page ≥ 42,949,673 wrapped negative and served page one; past-the-end redirect never fired (`BrowseQueryService.cs:155`, `Browse.razor:235`) | Unchecked multiply → clamp before multiplying; sign tested before `- 1` | `A_page_far_past_the_end_returns_no_rows_rather_than_wrapping_to_the_first` |
| BUG-018 | `ranked=2019-13` meant all of 2019, hiding the typo (`BrowseSearch.cs:160-161`) | Silent per-part fallback → every present part must be valid | `Malformed_dates_are_searched_as_text` (6 cases) |
| BUG-019 | `PenaliseAsync` only delayed the caller; the bucket kept its tokens (`OsuRateLimiter.cs:35`) | Implementation did not match its doc → drain, then wait | `Penalise_drains_the_bucket_so_the_retry_waits_for_the_next_window` |
| BUG-020 | Sync page printed "N / 60" whatever the configured rate (`SyncStatus.razor:22`, `:27`) | Magic number → reads `IOptions<OsuApiOptions>` | `The_sync_page_budget_reads_the_configured_rate` |
| BUG-021 | Overlapping loads: Browse showed the previous filter's rows under new chips; a mode page showed another mode's star bands; "show more" appended to the wrong mode's list (`Browse.razor:248`, `ModeDetail.razor:188`, `:212`) | Per-await publishing → a load counter; publish only the newest | Three bUnit tests that release gated DB calls in reverse order |
| BUG-023 | `TryGetProperty` throws on non-objects: the backfill's array branch was unreachable; one bad element failed a whole poll or sync (`ScoreBackfillJob.cs:227`, `RecentScoresJob.cs:78`, `PlayCountSyncJob.cs:68`) | Assumed TryGetProperty is total → check `ValueKind` first | Three skip-not-fail tests |
| BUG-024 | Both limiters keyed on the full client address, so an IPv6 client could rotate through its /64 and start every budget over (`Program.cs:70`, `:89`) | An address treated as an identity → `ClientAddress` keys IPv6 by /64, IPv4 and v4-mapped IPv6 by address | `Every_address_in_one_ipv6_slash64_is_one_client`, `A_v4_mapped_address_counts_as_the_ipv4_client_it_is`, plus 3 boundary pins |
| BUG-025 | `If-None-Match` compared as one raw string: a proxy's `W/"…"`, a list of tags and `*` each got the whole badge instead of a 304 (`BadgeEndpoints.cs:149`) | Hand-rolled comparison → typed header; `*` or any tag, compared weakly (RFC 9110 §13.1.2) | `A_tag_weakened_by_a_proxy_still_revalidates`, `…list_of_tags…`, `…star…`; malformed values still get a 200 |
| BUG-026 | The SVG, a document on the app's origin built from query input, had no CSP, and neither format had `nosniff` (`BadgeEndpoints.cs:147`). No injection was found: this is depth | No hardening on the one input-built public document → `default-src 'none'` and `nosniff`; both layouts checked in headless Chromium | `The_svg_can_run_nothing_even_when_opened_as_a_page`, `The_png_is_not_sniffed_as_anything_else` |
| BUG-027 | README: "`sync recent` … is all you need"; Sync page: "keeps everything current" (`README.md:140`, `SyncStatus.razor:91`). The poll asks for passes only and never writes playcounts | Guidance for Passed read as covering every rung → all three timers, and why | Documentation; checked against `RecentScoresJob` and `ProgressQueryService.Attempted` |

---

## 5. Fixes, tests and validation

### Test-first evidence

Every bug-specific test was run against the original code and failed for the diagnosed
reason, then passed after the fix. For BUG-001 to BUG-023 the test came first. For BUG-025
and BUG-026 the original endpoint file (unchanged on the branch until then) was put back
to show it:

| Bug(s) | Observed failure before the fix |
|---|---|
| 001 | `Words.Count` 5,000 (expected 16); 1,200 words → `SqliteException` |
| 002 | `SqliteException: no such column: NaN / Infinity` |
| 003 | `ArgumentOutOfRangeException` on the call after `Invalidate()` |
| 004, 016 | 3 maps Departed after `--max-pages 1`; ItemsDone 10 instead of 5 |
| 005 | `TaskCanceledException` from the bookkeeping write; job left Running |
| 006 | Ladder `(5,2,1,2)` vs truth `(5,2,2,1)`; "Untouched: chip says 2, list shows 1" |
| 007 | Pass behind 100 newer fails never stored |
| 008, 009 | PlayedAt moved to today; backfill dated by best score; IsLazer stayed false |
| 010 | 40/40 maps attempted with revoked credentials, job Completed |
| 011 | 403 still retrying when the 5 s deadline hit |
| 012, 013 | Two different SVGs with ETag `"1b8127a85fea2de2fa57"`; `UPDATED 2569-…` under th-TH |
| 014, 015 | `sync recent` with UserId 0 exited 0; `--mode ctb` exited 0 after starting an all-mode backfill |
| 017, 018, 019, 020 | Page one returned for page 50,000,000; year filter for `2019-13`; 10 permits left; "60 / 60" |
| 021 | Browse showed `[2, 4]` (attempted) under Untouched; bands lost; "Mania Set" on the taiko page |
| 022, 023 | 4/5 maps Departed after a 404 or `{}`; `InvalidOperationException` on a non-object element |
| 024 | `2001:db8:1:2::1` and `2001:db8:1:2:ffff:ffff:ffff:fffe` keyed as two clients; `::ffff:203.0.113.5` keyed apart from `203.0.113.5` |
| 025, 026 | `W/` tag, two-tag list and `*` each answered 200 instead of 304; no `Content-Security-Policy` or `X-Content-Type-Options` header |

BUG-024's fix is a new helper, so its tests ran against the original keying expression
(`RemoteIpAddress?.ToString() ?? "unknown"`) substituted into that helper: 2 of 5 failed,
and the other 3 are boundary pins. BUG-027 changes documentation only and has no test.

One hypothesis was disproved this way. A first mode-page race test **passed** against the
original code: that page re-reads its `_mode` field after every await, so a stale load
fetches the *new* mode's data. The real race needs the switch to land while a query after
the first is in flight. The test was corrected to hold exactly that query.

### Test infrastructure (`tests/OsuTracker.Web.Tests`)

- **`TestDatabase`**: a real SQLite file per test, built by the app's own migrations, so
  every query is checked by the provider the app ships with. That matters: BUG-002 lives in
  SQLite's handling of SQL literals and would not reproduce on an in-memory provider.
- **`FakeOsuServer` / `OsuApiHarness`**: replaces osu.ppy.sh at the `HttpMessageHandler`
  level, so the real API client, token provider and rate limiter run unmodified. No
  credentials are read and no request leaves the machine.
- **`GatedDbFactory`**: holds chosen DB context requests until the test releases them, which
  makes UI races deterministic. The page tests passed 15 of 15 consecutive runs.
- **`BadgeEndpointTests`**: the badge endpoints in a real ASP.NET Core pipeline on an
  in-memory `TestServer`. A hook delivers a raw `If-None-Match` line the way Kestrel does,
  because `HttpClient` splits a typed list before the server sees it, which would let a
  broken list comparison pass.
- **Behaviour pins**: 22 further tests lock down existing correct behaviour, so the fixes
  cannot move it. They cover reconciliation of a finished sweep, convert and pre-rank
  rejection, star-band semantics, quoted search terms, SVG well-formedness, accent
  sanitisation, real PNG output, token sharing, and others. The second pass added six:
  a matching tag still revalidates (SVG and PNG), stale or malformed tags still get the
  badge, `refresh=1` from the internet is still served from the cache, and IPv4 clients,
  neighbouring IPv6 networks and address-less requests keep their own keys.

### Regression and smoke testing

- Full suite: **123/123 passing**.
- Badge headers, in headless Chromium against the fixed build: the banner and slim layouts
  both render under `default-src 'none'`, with no CSP violations or console errors.
- End to end, against the original build and the fixed build. Both ran in the Production
  environment on a fresh database, and every cell was measured:

| Request | Original `613e19d` | Fixed |
|---|---|---|
| `/browse?q=` + 2,500 words | connection dropped; process aborted (exit 134); every later request refused | 200 |
| `/browse?q=stars>nan` | 500 | 200 |
| `/browse?q=` + 700 × `artist=x` | 200 | 200 |
| `/browse?page=-2147483648`, `?page=50000000` | 200 | 200 |
| `/`, `/modes/fruits`, `/browse`, `/sync`, `/badge` | 200 | 200 |
| `/badge.svg`, `/badge.png`, `/badge/fruits.png?scale=4…` | 200 | 200 (PNG checked: 1760×420) |
| `/modes/nonsense`, `/not-a-page` | 404 | 404 |

  The fixed server stayed up throughout, with no `fail:` entries in its log. On an empty
  database the page-number requests can only show their status code; the wrong rows they
  served before the fix (BUG-017) are shown by the unit test, which seeds data.
- CLI against the built binary: `sync status` → 0; missing credentials → 2 with a message;
  missing UserId → 2; `--mode ctb` → 2; `--limit ten` → 2; unknown command → usage and 2.

### Static analysis

- Default analysers: **0 warnings** across the solution, now enforced by CI with
  `-warnaserror`.
- `AnalysisMode=All` on the application project: 334 warnings, triaged. None indicates a defect. The bulk is
  `ConfigureAwait` (CA2007, not applicable to ASP.NET Core), null-argument checks already
  covered by nullable reference types (CA1062), and logging-argument cost (CA1873).
  `int.ToString()` without a culture (CA1305) only touches positive cursor ids, so it has no
  effect. The catch-all in the rasteriser (CA1031) is intentional and documented.

### Dependency audit

No known vulnerabilities in the app's dependencies. One was caught and avoided in a
*candidate* test dependency: bUnit 2.2.2 pulls in AngleSharp 1.4.0 (GHSA-pgww-w46g-26qg,
fixed in 1.5.0), so bUnit is pinned to 2.10.3. CI now fails the restore on any moderate or
worse advisory. The gate was proven against that exact package. The second pass added
`Microsoft.AspNetCore.TestHost` 10.0.11, to the test project only, and the audit is still clean.

### Performance (BUG-006 changes a hot query)

Synthetic catalogue: 215,000 maps, 17,400 playcounts, 10,000 scores. Median of 9 runs, warm cache.

| Query | Original | First fix (`EXISTS` on `b.Id`) | Final fix |
|---|---|---|---|
| `GetAllModesAsync` | 79.8 ms | 115.5 ms | 82.0 ms |
| `GetStarBandsAsync(osu)` | 110.1 ms | 131.9 ms | 108.2 ms |
| `GetStarBandsAsync(fruits)` | 30.0 ms | 34.2 ms | 32.5 ms |
| `GetStatusCountsAsync(osu)` | 62.3 ms | 81.9 ms | 60.0 ms |
| `GetStatusCountsAsync(fruits)` | 15.3 ms | 20.1 ms | 18.9 ms |

`EXPLAIN QUERY PLAN` showed why the first form was slower. Keyed on `b.Id`, the
`NOT EXISTS` ran for every counted map (215,000 probes) before PlayCounts was consulted.
Correlated on `p.BeatmapId`, it can only run after a playcount matched (17,400 probes). The
final form is at parity with the original and gives the right answer.

---

## 6. Observations

These need an owner decision, live-API verification, or are features rather than defects.
Three were later promoted to defects and fixed, and one in part; they keep their IDs so
earlier references still resolve. Fifteen remain open.

| ID | Area | Observation and recommendation |
|---|---|---|
| OBS-01 | Integration | The token bucket grants all 60 tokens at once each minute. The spike validated 60/min with *evenly spaced* requests (`Task.Delay(900)`). Within a burst the instantaneous rate can exceed the ~200/min at which the spike saw connections dropped. Consider one token every 60/RequestsPerMinute seconds. Not changed: osu.ppy.sh is unreachable from the sandbox to confirm. |
| OBS-02 | Design | Maps ranked between monthly catalogue syncs are not in the catalogue when played. `sync recent` skips those passes, and the one-time backfill never returns to them, so they are lost. Suggest recording a first-seen date per map and adding a `sync scores --new` for newly added maps. |
| OBS-03 | Data | `CountsAsPass` is not recomputed when a set's RankedDate changes (unranked and re-ranked sets). |
| OBS-04 | UI | The Sync page budget reads the *web host's* limiter, which never makes requests. The CLI runs in another process, so the meter is always full. |
| OBS-05 | UI | The nav shows "sync failed" while any job row is Failed, including a per-mode backfill row superseded by a later full run. |
| OBS-06 | Performance | `PRAGMA synchronous=NORMAL` is per connection; with pooling it reaches one connection only. Use a connection interceptor if it matters. |
| OBS-07 | Performance | Numeric search values are inlined as SQL constants, so each distinct value compiles a new EF query. Parameterise them. |
| OBS-08 | Security | **Fixed as BUG-024.** Rate-limit partitions keyed on the full IP; IPv6 clients could rotate within their /64. |
| OBS-09 | Security | **Badge part fixed as BUG-026.** The pages still send no CSP, `frame-ancestors` or `X-Content-Type-Options`. Their CSP has to admit Blazor's script and WebSocket and the Google Fonts origins (OBS-14), so it needs building and testing against a live circuit. Whether the app may be framed is the owner's call. |
| OBS-10 | Security | The Badge page's "refresh data" forces a full rebuild for anonymous visitors, bypassing the endpoint's `IsLocal` guard. The cost is comparable to a page view. |
| OBS-11 | HTTP | **Fixed as BUG-025.** `If-None-Match` was compared verbatim: lists, `*`, and weak validators added by proxies never matched. |
| OBS-12 | Data | `most_played` is paged by offset over an ordering that changes as you play. Syncing mid-session can skip or repeat rows. |
| OBS-13 | CLI | A second Ctrl+C cannot force-quit (`e.Cancel = true` always). |
| OBS-14 | Privacy | Pages load Google Fonts, so every visitor's IP goes to Google. Self-host the fonts to keep the README's "only API calls leave the box". |
| OBS-15 | Dependencies | Patch updates: EF Core 10.0.11 → 10.0.13; SkiaSharp.NativeAssets.Linux 4.148.0 → 4.153.1 (move SkiaSharp itself in step); spike configuration packages 10.0.0 → 10.0.13. None is security-relevant. |
| OBS-16 | Spike (archival) | Probe P6c (`Probes.cs:447-456`) reports PASS for `s=approved` whenever the request succeeds. osu! ignores that filter and returns the ranked sweep, as `CatalogSyncJob`'s own comment records, so the verdict is a false positive. Left unchanged: the spike is kept as a record. |
| OBS-17 | Spike (archival) | The BUG-023 pattern recurs in the spike (`Probes.cs:379`, `DeepProbe.cs:92`). Its client also builds an unused request (`OsuApiClient.cs:64-66`), never disposes the `JsonDocument` it clones from, and does not retry its own timeouts. Harmless for a one-off probe; left unchanged. |
| OBS-18 | Docs | **Fixed as BUG-027.** The README said `sync recent` is "all you need" once backfill is done. But Attempted only moves when `sync playcounts` runs again: the poll records passes, never fails. |

---

## 7. Patterns and prevention

### Recurring patterns behind these bugs

1. **Public input without bounds** (001, 002, 017, 018). Treat every query-string value as
   hostile: cap counts and lengths, require finite numbers, clamp before arithmetic.
2. **Cleanup that honours the token it exists to record** (005). State writes made in
   `catch`/`finally` blocks, or after cancellation, must use `CancellationToken.None`.
3. **Anomalies treated as normal termination before a destructive step** (004, 022). Any
   step that can mark data as gone (reconcile, delete) needs a positive signal that the input
   was complete. "The loop ended" is not one.
4. **Counts derived by subtraction across independent sources** (006). Count the set you
   mean, using the same predicate as the list that displays it, and add a test asserting
   chip count = list count.
5. **One field serving two purposes** (008). "The score on show" and "when it was passed"
   diverge; give each its own definition.
6. **Copy-pasted mapping blocks** (009). One helper per mapping.
7. **Swallow-and-continue without a breaker** (010, 003). Skip the isolated failure, stop on
   a streak, and never report success with nothing done.
8. **Async UI publishing per await** (021). Gather results, then publish only if the load is
   still current.
9. **Culture-sensitive formatting of machine- or public-facing output** (013). Use the
   invariant culture by default. The codebase already did this for numbers; dates were missed.
10. **Unverified assumptions about external APIs** (007, 023). Encode the provider's contract
    in a test fake, citing the source, as `OsuWebRecent` now does.
11. **Protocol details done by hand** (024, 025). Read headers through the framework's typed
    accessors and compare them as the RFC says. Key IPv6 clients by prefix, not by address.

### Process and tooling

- **CI is in place** (`.github/workflows/ci.yml`): restore with a vulnerability gate, build
  with warnings as errors, run tests. Actions are pinned by commit SHA. Make it a required
  check on `main`.
- Add **Dependabot or Renovate** for NuGet and GitHub Actions. The pinned SHAs and OBS-15
  need regular bumps.
- Keep `--max-pages` a developer-only flag, or document it alongside the reconcile behaviour.
- Use the patterns above as a review checklist for any change to sync jobs or public endpoints.

### Monitoring and logging

- Run the web host under systemd with `Restart=on-failure`. A future crash class would then
  cost seconds, not the service.
- Add a small health endpoint that reports job freshness, for example unhealthy when
  `RecentScores` has not succeeded in six hours, so a timer that silently stops working
  (BUG-014's failure mode) is noticed.
- Alert when a job finishes **Failed**, or **Completed with errors > 0**. The backfill logs
  its error count.
- Log rate-limit rejections, and search truncations (BUG-001's bound), with their client
  partition, to make probing visible.

---

## 8. Assumptions, constraints and audit trail

- **No live API calls were made**: osu.ppy.sh is unreachable from the analysis sandbox, and
  rate limits were respected by construction. API behaviour comes from osu-web's source
  (cited above) and the spike's documented findings. The test fakes implement it.
- The .NET 10 SDK was installed into the sandbox only, from Microsoft's own package feed.
- **One branch, not one per fix**, because the session was restricted to
  `claude/repo-bug-analysis-fixes-mbzyx7`. The audit trail is instead one commit per fix
  group. Each message names its bug IDs, root cause and impact.
- **Versioning.** The application has no versioned public API. User-visible behaviour
  changes are listed in the executive summary. The badge URLs are the closest thing to
  one: BUG-025 and BUG-026 only add headers and answer more revalidations with 304, so an
  embedder that ignores both gets the same images. Under semantic versioning that is a
  patch.
- **A /64 per IPv6 client (BUG-024)** is the usual allocation, not a guarantee. A network
  that hands one customer a /56 still lets that customer spread across 256 budgets, and a
  provider that puts unrelated customers in one /64 makes them share one. Bucketing wider
  would make the second case much more common, so /64 is the deliberate trade-off.
- No secrets were read or written. Tests use fake credentials, and nothing personal was added
  to the repository.

### Commit log (oldest first)

| Commit | Change |
|---|---|
| `6d1c045` | test: test project with SQLite and fake osu! API harness |
| `445b3a6` | fix(browse): bound and validate search input (001, 002, 017, 018) |
| `4b6cdf4` | fix(api): 401 recovery, no retry on client errors, drain on 429 (003, 011, 019) |
| `0ff2429` | fix(sync): cancels as Paused, no partial reconcile, failure breaker (004, 005, 010, 016) |
| `d3c2816` | fix(cli): reject bad flags and missing settings; exit 130 on Ctrl+C (014, 015, 005) |
| `3837077` | fix(sync): real end-of-catalogue detection; JSON shape guards (022, 023) |
| `d93b7b3` | fix(progress): count Attempted directly (006) |
| `ba1775f` | fix(scores): passes-only poll, first-clear dating, display helper (007, 008, 009) |
| `0a0bbf8` | fix(badge): drawn date in the ETag, invariant dates (012, 013) |
| `f06471d` | fix(pages): newest load wins; configured budget (020, 021) |
| `4a601a1` | perf(progress): Attempted at parity with the original query (006) |
| `d00563f` | test(badge): PNG really rasterises |
| `c0676e2` | ci: build, test, dependency audit |
| `6931c13` | docs: README test section |
| `71b6f42` | docs: this report, `bugs.json`, `bugs.csv` |
| `a627d3a` | fix(security): rate-limit IPv6 clients per /64 (024) |
| `f00c7c1` | fix(badge): RFC 9110 revalidation; CSP and `nosniff` on badges (025, 026) |
| `286dc0c` | docs: `sync playcounts` keeps running after the backfill (027) |
| *(this commit)* | docs: report, `bugs.json`, `bugs.csv` updated for 024–027 |
