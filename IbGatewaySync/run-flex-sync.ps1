$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$configPath = Join-Path $projectDir "appsettings.json"
$logDir = Join-Path $projectDir "logs"
$logPath = Join-Path $logDir "flex-sync.log"

function Write-Log {
    param([string]$Message)

    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    "[$timestamp] $Message" | Add-Content -Path $logPath -Encoding UTF8
}

function Invoke-FlexXml {
    param(
        [Parameter(Mandatory = $true)][string]$Uri
    )

    [xml](Invoke-WebRequest -UseBasicParsing -Uri $Uri | Select-Object -ExpandProperty Content)
}

function Get-FirstByLocalName {
    param(
        [Parameter(Mandatory = $true)]$Node,
        [Parameter(Mandatory = $true)][string]$LocalName
    )

    @($Node.ChildNodes) | Where-Object { $_.LocalName -eq $LocalName } | Select-Object -First 1
}

function Get-AttributeValue {
    param(
        [Parameter(Mandatory = $true)]$Node,
        [Parameter(Mandatory = $true)][string]$AttributeName
    )

    $attribute = $Node.Attributes | Where-Object { $_.LocalName -eq $AttributeName } | Select-Object -First 1
    if ($null -eq $attribute) {
        return $null
    }

    return $attribute.Value
}

function To-DecimalOrNull {
    param($Value)

    if ([string]::IsNullOrWhiteSpace([string]$Value)) {
        return $null
    }

    $parsed = 0.0
    if ([decimal]::TryParse([string]$Value, [ref]$parsed)) {
        return $parsed
    }

    return $null
}

New-Item -ItemType Directory -Force -Path $logDir | Out-Null
Write-Log "Starting IB Flex sync..."

if (-not (Test-Path $configPath)) {
    throw "Missing config file: $configPath"
}

$config = Get-Content -Raw $configPath | ConvertFrom-Json
$flex = $config.FlexWebService
$worker = $config.Worker

if ([string]::IsNullOrWhiteSpace($flex.Token)) {
    throw "FlexWebService.Token is required."
}

if ([string]::IsNullOrWhiteSpace($flex.QueryId)) {
    throw "FlexWebService.QueryId is required."
}

if ([string]::IsNullOrWhiteSpace($worker.IngestUrl)) {
    throw "Worker.IngestUrl is required."
}

if ([string]::IsNullOrWhiteSpace($worker.IngestToken)) {
    throw "Worker.IngestToken is required."
}

$baseUrl = [string]$flex.BaseUrl
if (-not $baseUrl.EndsWith('/')) {
    $baseUrl += '/'
}

$sendUri = "{0}SendRequest?t={1}&q={2}&v={3}" -f $baseUrl,
    [uri]::EscapeDataString([string]$flex.Token),
    [uri]::EscapeDataString([string]$flex.QueryId),
    [string]$flex.Version

$sendXml = Invoke-FlexXml -Uri $sendUri
$sendStatus = (Get-FirstByLocalName -Node $sendXml.DocumentElement -LocalName "Status").InnerText
if ($sendStatus -ne "Success") {
    $errorCode = (Get-FirstByLocalName -Node $sendXml.DocumentElement -LocalName "ErrorCode").InnerText
    $errorMessage = (Get-FirstByLocalName -Node $sendXml.DocumentElement -LocalName "ErrorMessage").InnerText
    throw "Flex SendRequest failed. Error ${errorCode}: $errorMessage"
}

$referenceCode = (Get-FirstByLocalName -Node $sendXml.DocumentElement -LocalName "ReferenceCode").InnerText
if ([string]::IsNullOrWhiteSpace($referenceCode)) {
    throw "Flex SendRequest succeeded but did not return a reference code."
}

Start-Sleep -Seconds ([int]$flex.PollDelaySeconds)

$statementUri = "https://gdcdyn.interactivebrokers.com/AccountManagement/FlexWebService/GetStatement?t={0}&q={1}&v={2}" -f
    [uri]::EscapeDataString([string]$flex.Token),
    [uri]::EscapeDataString([string]$referenceCode),
    [string]$flex.Version

$statementXml = Invoke-FlexXml -Uri $statementUri

$symbolSet = @{}
foreach ($symbol in $flex.Symbols) {
    if (-not [string]::IsNullOrWhiteSpace([string]$symbol)) {
        $symbolSet[[string]$symbol.ToUpperInvariant()] = $true
    }
}

$positions = New-Object System.Collections.Generic.List[object]
$openPositions = $statementXml.GetElementsByTagName("OpenPosition")

foreach ($position in $openPositions) {
    $ticker = [string](Get-AttributeValue -Node $position -AttributeName "symbol")
    if ([string]::IsNullOrWhiteSpace($ticker)) {
        $ticker = [string](Get-AttributeValue -Node $position -AttributeName "underlyingSymbol")
    }

    if ([string]::IsNullOrWhiteSpace($ticker)) {
        continue
    }

    $ticker = $ticker.Trim().ToUpperInvariant()
    if (-not $symbolSet.ContainsKey($ticker)) {
        continue
    }

    $price = To-DecimalOrNull (Get-AttributeValue -Node $position -AttributeName "markPrice")
    if ($null -eq $price -or $price -le 0) {
        $price = To-DecimalOrNull (Get-AttributeValue -Node $position -AttributeName "price")
    }

    if ($null -eq $price -or $price -le 0) {
        continue
    }

    $quantity = To-DecimalOrNull (Get-AttributeValue -Node $position -AttributeName "position")
    if ($null -eq $quantity) {
        $quantity = To-DecimalOrNull (Get-AttributeValue -Node $position -AttributeName "quantity")
    }

    $payloadPosition = [ordered]@{
        ticker = $ticker
        name = [string](Get-AttributeValue -Node $position -AttributeName "description")
        currency = [string](Get-AttributeValue -Node $position -AttributeName "currency")
        pricePerShare = [decimal]$price
    }

    if ([bool]$flex.SyncQuantities -and $null -ne $quantity) {
        $payloadPosition.quantity = [decimal]$quantity
    }

    $positions.Add([pscustomobject]$payloadPosition)
}

if ($positions.Count -eq 0) {
    throw "Flex statement did not contain any matching symbols."
}

$payload = [ordered]@{
    source = "IBKR Flex Web Service"
    notes = "Snapshot updated from Flex Query $($flex.QueryId)."
    asOfDate = [DateTime]::UtcNow.ToString("o")
    refreshIntervalMinutes = 1440
    positions = $positions
}

$headers = @{
    Authorization = "Bearer $($worker.IngestToken)"
}

$body = $payload | ConvertTo-Json -Depth 6
Invoke-RestMethod -Method Post -Uri $worker.IngestUrl -Headers $headers -ContentType "application/json" -Body $body | Out-Null

Write-Log "Sync finished successfully. Updated $($positions.Count) symbols from query $($flex.QueryId)."
