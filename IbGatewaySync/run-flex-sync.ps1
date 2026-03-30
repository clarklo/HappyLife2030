$ErrorActionPreference = "Stop"

$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$logDir = Join-Path $projectDir "logs"
$logPath = Join-Path $logDir "flex-sync.log"

New-Item -ItemType Directory -Force -Path $logDir | Out-Null

$timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
"[$timestamp] Starting IB Flex sync..." | Add-Content -Path $logPath -Encoding UTF8

Push-Location $projectDir
try {
    dotnet run --project (Join-Path $projectDir "IbGatewaySync.csproj") *>> $logPath
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    "[$timestamp] Sync finished successfully." | Add-Content -Path $logPath -Encoding UTF8
}
catch {
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    "[$timestamp] Sync failed: $($_.Exception.Message)" | Add-Content -Path $logPath -Encoding UTF8
    throw
}
finally {
    Pop-Location
}
