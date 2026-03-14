# 2030 Retirement Board

Static retirement dashboard built with .NET 9 Blazor WebAssembly.

Current default scenario:

- Start date: 2026-03-15
- Initial deployment: NT$40,000,000
- Basket: CSNDX, TSM, VWRA, CSPX
- Goal: NT$150,000 monthly passive income by 2030-12-31

## Run locally

```powershell
dotnet restore --configfile .\NuGet.Config
dotnet run
```

## Update daily data

Edit:

`wwwroot/data/retirement-plan.json`

Main fields:

- `asOfDate`
- `positions[].quantity`
- `positions[].pricePerShare`
- `positions[].annualDividendPerShare`
- `history`

## Deploy to GitHub Pages

Workflow file:

`.github/workflows/deploy.yml`

After pushing, open repository `Settings > Pages` and set source to `GitHub Actions`.
