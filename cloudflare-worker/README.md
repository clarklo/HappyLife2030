# Cloudflare Worker

This worker reads the base allocation from your deployed Pages site, refreshes market quotes every 2 hours, and returns a live `retirement-plan.json` payload for the dashboard.

## Required dashboard setup

1. Create a Worker from the `cloudflare-worker` folder.
2. Add a KV binding named `PLAN_CACHE`.
3. Add a Worker secret named `LEEWAY_API_TOKEN`.
4. Add a Worker secret named `WORKER_INGEST_TOKEN` for trusted local sync jobs.
5. Keep these environment variables:
   - `PAGES_BASE_URL=https://happylife2030.pages.dev`
   - `CORS_ORIGIN=https://happylife2030.pages.dev`
   - `USD_TWD_RATE=32`
6. Set a cron trigger to run every 2 hours.
7. After the Worker is deployed, copy the Worker URL and put it in `wwwroot/data/runtime-config.json` as `liveApiUrl`.

## API routes

- `GET /api/plan`: return the cached live plan snapshot.
- `GET /api/refresh`: force one refresh immediately.
- `POST /api/ingest`: accept an authenticated snapshot update from the local `.NET` sync tool.
- `GET /health`: simple health check.

## Notes

- The Worker now uses Leeway for price quotes.
- The free Leeway plan only gives 50 requests per day, so the default schedule is every 2 hours for 4 tickers.
- `TSM` still uses the configured annual cash dividend from the base plan snapshot.
- `CSNDX`, `CSPX`, and `VWRA` are treated as accumulating share classes, so their direct cash dividend in the dashboard remains `0` until you choose a distributing replacement.
