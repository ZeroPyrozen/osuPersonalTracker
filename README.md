# osu!PersonalTracker

**How much of osu! have you actually played?**

Your osu! profile tells you your rank, your pp, and your top 100 plays. What it doesn't
tell you is how much of the game is left — of the ~215,000 ranked and approved
difficulties in existence, which ones have you passed, which have you only attempted,
and which have you never touched at all?

osu!PersonalTracker answers that. It builds a local copy of the entire ranked catalogue,
matches it against your complete play history, and gives you a straight completion
percentage for every mode — plus a badge you can put on your profile.

It's self-hosted and single-user. Everything runs on your machine, against your own
account, and the only thing that leaves the box is the API calls to `osu.ppy.sh`.

---

## What you get

**A completion ladder.** Every map you've passed, attempted, or never opened, broken
down by mode. The headline number is the one nobody else shows you: the share of the
ranked catalogue you've actually cleared.

**A browsable backlog.** Filter the catalogue by pass state to find what's left — which
sets you're one difficulty away from completing, and what you attempted once and
abandoned.

**An embeddable badge.** A live SVG or PNG of your completion, in banner or slim layout,
that you can drop into an osu! profile, a GitHub README, or anywhere else that renders
an image.

## How completion is counted

The rules are deliberately strict, and are applied in exactly one place in the code so
every screen agrees:

- **Ranked and Approved only.** Loved, Qualified and Pending maps don't count toward
  anything.
- **Native difficulties only.** A converted map counts for the mode it was made for,
  not the mode you played it in.
- **A pass is a pass.** Mods included — a NoFail clear with mediocre accuracy counts
  exactly as much as an S rank.
- **Only plays from after the map was ranked.** Clearing something while it sat in
  Qualified doesn't count toward the ranked catalogue.

*Attempted* is approximate and shown with a `≈`. It comes from osu!'s playcount data,
which carries no timestamps, so a map you played once before it was ranked can't be
filtered out the way a score can.

---

## Getting started

You'll need the **.NET 10 SDK** and an osu! account.

### 1. Create an osu! OAuth application

Go to [your osu! account settings](https://osu.ppy.sh/home/account/edit) → *OAuth* →
**New OAuth Application**. Name it anything; leave the callback URL blank, since this
app doesn't use one.

Keep the **Client ID** and **Client Secret**. You also need your numeric **user id** —
it's the number in your profile URL, `osu.ppy.sh/users/`**`1234567`**.

### 2. Store your credentials

These go in .NET user secrets, which are stored outside the repository so they can't be
committed by accident. From `src/OsuTracker.Web/`:

```bash
dotnet user-secrets set "OsuApi:ClientId" "12345"
```

```bash
dotnet user-secrets set "OsuApi:ClientSecret" "your-secret-here"
```

```bash
dotnet user-secrets set "OsuApi:UserId" "1234567"
```

The `OsuApi:` prefix is required — the app reads its settings from that configuration
section. If you'd rather use environment variables, `OsuApi__ClientId` and friends work
the same way. Don't put credentials in `appsettings.json`; that file is committed.

### 3. Fill the database

Sync runs headless, without starting the web server, because the first two steps take a
while. Run these from `src/OsuTracker.Web/`, in order:

```bash
dotnet run -- sync catalog
```

Downloads every ranked and approved beatmapset. This is the slow one — it's the whole
catalogue — but you only repeat it monthly, as new maps get ranked.

```bash
dotnet run -- sync playcounts
```

Pulls your play history. Fast, and it tells the next step which maps are worth checking.

```bash
dotnet run -- sync scores
```

Fetches your actual scores for the maps you've played. Roughly one request per map, so
expect it to run for a while. It's resumable — if it dies, add `--resume`.

Requests are capped at 60/minute. osu! advertises a much higher ceiling, but sustained
paging above ~200/minute gets the connection dropped, so the tracker stays well under.

### 4. Run it

```bash
dotnet run --project src/OsuTracker.Web
```

Open **https://localhost:7090**.

| | |
|---|---|
| `/` | Completion ladder across all modes |
| `/modes/{mode}` | One mode in detail, including nearly-finished sets |
| `/browse` | The catalogue, filtered by pass state |
| `/sync` | Job status and when things last ran |
| `/badge` | Badge preview and embed-URL builder |

### 5. Keep it current

```bash
dotnet run -- sync recent
```

Four requests — one per mode — covering the last 24 hours. Once the initial backfill is
done, this is all you need; put it on a timer and the tracker stays up to date on its
own. Re-run `sync catalog` every month or so to pick up newly ranked maps.

`dotnet run -- sync status` shows job state and current progress without fetching
anything.

---

## The badge

Build a URL on the `/badge` page, or construct one directly:

```
/badge.svg              /badge.png
/badge/{mode}.svg       /badge/{mode}.png
```

Options: `mode` (`osu`, `taiko`, `catch`, `mania`, or omit for everything combined),
`layout` (`banner` or `slim`), `theme` (`dark` or `light`), `accent` (a mode name,
`gold`, `mint`, or any `#rrggbb`), and for PNGs, `scale` (1–4).

Use the PNG on an osu! profile — osu!'s image proxy only re-serves raster formats. The
SVG is sharper everywhere else.

## Project layout

```
src/OsuTracker.Web/       The application — Blazor Server, EF Core, SQLite
spike/OsuTracker.Spike/   A throwaway API spike from before the schema existed
```

Your data lives in `src/OsuTracker.Web/tracker.db`, created automatically on first run.
It's gitignored, along with credentials and sync logs — nothing personal ends up in the
repository.

The spike is kept for the record. It answered the two API questions the design depends
on; you don't need it to run the tracker. See [spike/README.md](spike/README.md) if
you're curious.

## License

Apache License 2.0 — see [LICENSE](LICENSE).
