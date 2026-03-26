# 2030 Retirement Board

Static retirement dashboard built with .NET 9 Blazor WebAssembly.

Current default scenario:

- Start date: 2026-03-27
- Total assets: NT$45,000,000
- Fixed deposit safety bucket: NT$15,000,000
- Growth basket: TSM, CSNDX, CSPX, VWRA
- Goal: NT$150,000 monthly passive income by 2030-12-31

## Run locally

```powershell
dotnet restore --configfile .\NuGet.Config
dotnet run
```

## Deploy to Cloudflare Pages

Cloudflare Pages build image does not include `dotnet` by default, so use the checked-in `build.sh` script.

Build settings:

- Build command: `bash ./build.sh`
- Build output directory: `output/wwwroot`

## Live pricing with Cloudflare Worker

A Worker implementation is included in [cloudflare-worker](./cloudflare-worker/README.md).

High-level flow:

1. Pages serves the static Blazor app.
2. Worker refreshes market prices every 5 minutes.
3. Worker stores the refreshed snapshot in KV.
4. The dashboard first tries the Worker API and falls back to the local JSON file if the live API is unavailable.

After the Worker is deployed, update `wwwroot/data/runtime-config.json` with the Worker URL:

```json
{
  "liveApiUrl": "https://your-worker-name.your-subdomain.workers.dev/api/plan"
}
```

## Update the base allocation

Edit:

`wwwroot/data/retirement-plan.json`

Main fields:

- `fixedDeposit.principalTwd`
- `positions[].quantity`
- `positions[].pricePerShare`
- `positions[].annualDividendPerShare`
- `history`
