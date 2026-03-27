# IB Gateway Sync

This local `.NET` tool connects to the IBKR Client Portal Gateway running on your own machine, reads your portfolio positions, and pushes a snapshot update to the Cloudflare Worker ingest API.

## What it updates

- By default it updates `pricePerShare` for matching symbols only.
- If you set `SyncQuantities=true`, it also updates `quantity`.
- It keeps the rest of your retirement dashboard snapshot unchanged.

## Setup

1. Start and log in to IBKR Client Portal Gateway on this machine.
2. Copy `appsettings.example.json` to `appsettings.json`.
3. Fill in:
   - `Worker.IngestUrl`
   - `Worker.IngestToken`
   - `IbGateway.AccountId` if you want to force a specific account
4. Run:

```powershell
dotnet run --project .\IbGatewaySync\IbGatewaySync.csproj
```

## Scheduling

For Windows Task Scheduler, run this command every day at 06:00:

```powershell
dotnet run --project D:\_CODEX_CLI\retirement-dashboard\IbGatewaySync\IbGatewaySync.csproj
```
