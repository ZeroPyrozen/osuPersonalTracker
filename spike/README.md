# Phase 1 spike

Answers the two API questions the design depends on, before any schema is written.

| | Question | Probe |
|---|---|---|
| **Rule 4** | Does the API expose passes that are **not on the leaderboard** — NoFail, low accuracy? | P4 |
| **Rule 5** | Does the all-scores endpoint exist, with timestamps, so pre-rank plays can be excluded without false negatives? | P5 |

Five supporting probes run alongside them: auth and native mode (P1), `ranked_date`
availability (P2), `most_played` shape (P3), catalog pagination and Approved coverage
(P6), and rate-limit headers (P7).

---

## 1. Get OAuth credentials

Go to **https://osu.ppy.sh/home/account/edit** → *OAuth* → **New OAuth Application**.

- Name: anything (`PersonalTracker spike`)
- Callback URL: leave blank — the client credentials grant doesn't use one

Copy the **Client ID** and **Client Secret**. You also need your numeric **user id**,
which is the number in your profile URL: `osu.ppy.sh/users/`**`1234567`**.

## 2. Store them

```bash
dotnet user-secrets set ClientId 12345
```

```bash
dotnet user-secrets set ClientSecret your-secret-here
```

```bash
dotnet user-secrets set UserId 1234567
```

Run these from `spike/OsuTracker.Spike/`. User secrets live outside the project
directory, so the secret never lands in source control.

## 3. Pick two test beatmaps — this part matters

The spike is only as good as the maps you point it at. A careless choice produces
`INCONCLUSIVE` and tells you nothing.

**`--nf-beatmap`** — a difficulty you passed with **NoFail**, ideally with mediocre
accuracy, on a map hard enough that your score is **nowhere near the top-50
leaderboard**. This is the whole point: if the endpoint is leaderboard-gated, this is
exactly the score it will hide. Picking a map where you have a strong leaderboard score
proves nothing.

**`--qualified-beatmap`** — a difficulty you played **before it was ranked**, while it
sat in Qualified or Pending. If you can't think of one, omit it; P5 falls back to the
NF map and still tells you whether `/all` exists, just not whether it spans the rank
date.

## 4. Run

```bash
dotnet run -- --mode fruits --nf-beatmap 123456 --qualified-beatmap 234567
```

Modes: `osu`, `taiko`, `fruits`, `mania`.

Every response is written to `bin/Debug/net10.0/probe-output/*.json` so you can read the
raw payloads rather than trusting the summariser.

Exit code is `0` when nothing failed, `3` when at least one probe failed, `1` on auth
failure, `2` on missing arguments.

---

## Reading the verdict

The run ends with a per-probe table and then this:

```
  Rule 4 (any pass counts, NF included) : IMPLEMENTABLE
  Rule 5 (only post-rank plays count)   : IMPLEMENTABLE
```

**Both green** — build the design exactly as written.

**P4 fails** (404 on a map you know you passed) — the score endpoint is leaderboard-gated
and your NF passes are invisible to backfill. This is the higher-impact failure of the
two: historical coverage becomes approximate, and the recent-scores poller becomes your
primary source going forward. The tracker still works, it just can't fully reconstruct
the past.

**P5 fails** (`/all` returns 404) — fall back to the best-score endpoint and accept false
negatives where a pre-rank best score hides a valid post-rank pass. Mostly affects
recently-ranked maps.

**Either is INCONCLUSIVE** — usually means the test map was a poor choice. Re-read step 3
and try another.

## Expected non-findings

Two probes are *designed* to fail, confirming assumptions the design already makes:

- **P3b** should report no timestamps on playcounts. That confirms Attempted must stay
  approximate and carry a `≈` in the UI.
- **P7** will likely find no rate-limit headers. Enforce the ~60 req/min ceiling
  client-side; you won't get server feedback until a 429.

## If auth fails

Client-credentials tokens act as a guest. If P3/P4/P5 come back `401`/`403` while P1
succeeds, the score endpoints require a user-scoped token and you'll need the
authorization code grant instead — more setup, but a known quantity. Note which probes
were rejected; that distinction is the useful signal.
