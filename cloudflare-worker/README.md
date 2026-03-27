# Cloudflare Worker

This worker reads the base allocation from your deployed Pages site, refreshes market quotes every 5 minutes, and returns a live `retirement-plan.json` payload for the dashboard.

## Required dashboard setup

1. Create a Worker from the `cloudflare-worker` folder.
2. Add a KV binding named `PLAN_CACHE`.
3. Add a Worker secret named `TWELVE_DATA_API_KEY`.
4. Keep these environment variables:
   - `PAGES_BASE_URL=https://happylife2030.pages.dev`
   - `CORS_ORIGIN=https://happylife2030.pages.dev`
   - `USD_TWD_RATE=32`
5. Set a cron trigger to run every 5 minutes.
6. After the Worker is deployed, copy the Worker URL and put it in `wwwroot/data/runtime-config.json` as `liveApiUrl`.

## API routes

- `GET /api/plan`: return the cached live plan snapshot.
- `GET /api/refresh`: force one refresh immediately.
- `GET /health`: simple health check.

## Notes

- The Worker now uses Twelve Data for price quotes.
- To keep a free Twelve Data key within daily limits, the scheduled job only refreshes symbols during their local market hours.
- `TSM` still uses the configured annual cash dividend from the base plan snapshot.
- `CSNDX`, `CSPX`, and `VWRA` are treated as accumulating share classes, so their direct cash dividend in the dashboard remains `0` until you choose a distributing replacement.
