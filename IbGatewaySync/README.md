# IB Flex Sync

This local `.NET` tool reads a saved IBKR Flex Query by using your Flex Web Service token and query id, then pushes a snapshot update to the Cloudflare Worker ingest API.

## What it updates

- By default it updates `pricePerShare` for matching symbols only.
- If you set `SyncQuantities=true`, it also updates `quantity`.
- It keeps the rest of your retirement dashboard snapshot unchanged.

## Setup

1. Copy `appsettings.example.json` to `appsettings.json`.
2. Fill in:
   - `FlexWebService.Token`
   - `FlexWebService.QueryId`
   - `Worker.IngestUrl`
   - `Worker.IngestToken`
3. Run:

```powershell
dotnet run --project .\IbGatewaySync\IbGatewaySync.csproj
```

## Notes

- This mode does not require Client Portal Gateway to be running.
- The Flex Query should include position rows with symbol and mark price fields.
- Flex report data is best for daily snapshots, not intraday real-time pricing.

## Scheduling

For Windows Task Scheduler, run this command every day at 06:00:

```powershell
dotnet run --project D:\_CODEX_CLI\retirement-dashboard\IbGatewaySync\IbGatewaySync.csproj
```
