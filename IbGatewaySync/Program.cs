using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;

var options = await SyncOptions.LoadAsync(Path.Combine(AppContext.BaseDirectory, "appsettings.json"));
var syncer = new FlexStatementSyncRunner(options);
var result = await syncer.RunAsync();

Console.WriteLine(result);

internal sealed record SyncOptions(
    FlexWebServiceOptions FlexWebService,
    WorkerOptions Worker)
{
    public static async Task<SyncOptions> LoadAsync(string configPath)
    {
        if (!File.Exists(configPath))
        {
            throw new InvalidOperationException($"Missing config file: {configPath}");
        }

        var json = await File.ReadAllTextAsync(configPath, Encoding.UTF8);
        var options = JsonSerializer.Deserialize<SyncOptions>(
            json,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

        if (options is null)
        {
            throw new InvalidOperationException("Unable to parse appsettings.json.");
        }

        if (string.IsNullOrWhiteSpace(options.FlexWebService.Token))
        {
            throw new InvalidOperationException("FlexWebService.Token is required.");
        }

        if (string.IsNullOrWhiteSpace(options.FlexWebService.QueryId))
        {
            throw new InvalidOperationException("FlexWebService.QueryId is required.");
        }

        if (string.IsNullOrWhiteSpace(options.Worker.IngestUrl))
        {
            throw new InvalidOperationException("Worker.IngestUrl is required.");
        }

        if (string.IsNullOrWhiteSpace(options.Worker.IngestToken))
        {
            throw new InvalidOperationException("Worker.IngestToken is required.");
        }

        return options;
    }
}

internal sealed record FlexWebServiceOptions
{
    public string BaseUrl { get; init; } = "https://ndcdyn.interactivebrokers.com/AccountManagement/FlexWebService";

    public string Token { get; init; } = string.Empty;

    public string QueryId { get; init; } = string.Empty;

    public int Version { get; init; } = 3;

    public string[] Symbols { get; init; } = ["TSM", "CSNDX", "CSPX", "VWRA"];

    public bool SyncQuantities { get; init; }

    public int PollDelaySeconds { get; init; } = 5;

    public int MaxPollAttempts { get; init; } = 12;

    public string UserAgent { get; init; } = "HappyLife2030FlexSync/1.0";
}

internal sealed record WorkerOptions
{
    public string IngestUrl { get; init; } = string.Empty;

    public string IngestToken { get; init; } = string.Empty;
}

internal sealed class FlexStatementSyncRunner(SyncOptions options)
{
    private static readonly HashSet<string> PendingErrorCodes =
    [
        "1001",
        "1003",
        "1004",
        "1005",
        "1006",
        "1007",
        "1008",
        "1009",
        "1019",
        "1021"
    ];

    public async Task<string> RunAsync()
    {
        using var flexClient = CreateFlexClient(options.FlexWebService);
        using var workerClient = CreateWorkerClient();

        var referenceCode = await RequestStatementAsync(flexClient, options.FlexWebService);
        var statementXml = await DownloadStatementAsync(flexClient, options.FlexWebService, referenceCode);
        var positions = ParsePositions(statementXml, options.FlexWebService.Symbols);

        if (positions.Count == 0)
        {
            throw new InvalidOperationException("Flex statement did not contain any matching symbols.");
        }

        var payload = new WorkerIngestPayload
        {
            Source = "IBKR Flex Web Service",
            Notes = $"Snapshot updated from Flex Query {options.FlexWebService.QueryId}.",
            AsOfDate = DateTime.UtcNow,
            RefreshIntervalMinutes = 24 * 60,
            Positions = positions
                .Select(position => new WorkerPositionUpdate
                {
                    Ticker = position.Ticker,
                    Name = position.Name,
                    Currency = position.Currency,
                    Quantity = options.FlexWebService.SyncQuantities ? position.Quantity : null,
                    PricePerShare = position.PricePerShare
                })
                .ToList()
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, options.Worker.IngestUrl)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Worker.IngestToken);

        using var response = await workerClient.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Worker ingest failed: {(int)response.StatusCode} {response.ReasonPhrase}{Environment.NewLine}{responseBody}");
        }

        return $"Flex sync completed. Updated {positions.Count} symbols from query {options.FlexWebService.QueryId}.";
    }

    private static HttpClient CreateFlexClient(FlexWebServiceOptions options)
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri(AppendTrailingSlash(options.BaseUrl))
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/xml"));
        return client;
    }

    private static HttpClient CreateWorkerClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    private static async Task<string> RequestStatementAsync(HttpClient client, FlexWebServiceOptions options)
    {
        var path = $"SendRequest?t={Uri.EscapeDataString(options.Token)}&q={Uri.EscapeDataString(options.QueryId)}&v={options.Version}";
        using var response = await client.GetAsync(path);
        var xml = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Flex SendRequest failed: {(int)response.StatusCode} {response.ReasonPhrase}{Environment.NewLine}{xml}");
        }

        var document = XDocument.Parse(xml);
        EnsureFlexSuccess(document, "SendRequest");

        var referenceCode = document.Root?
            .Elements()
            .FirstOrDefault(static element => element.Name.LocalName == "ReferenceCode")?
            .Value;

        if (string.IsNullOrWhiteSpace(referenceCode))
        {
            throw new InvalidOperationException("Flex SendRequest succeeded but did not return a reference code.");
        }

        return referenceCode;
    }

    private static async Task<string> DownloadStatementAsync(HttpClient client, FlexWebServiceOptions options, string referenceCode)
    {
        for (var attempt = 1; attempt <= options.MaxPollAttempts; attempt++)
        {
            var path = $"GetStatement?t={Uri.EscapeDataString(options.Token)}&q={Uri.EscapeDataString(referenceCode)}&v={options.Version}";
            using var response = await client.GetAsync(path);
            var xml = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Flex GetStatement failed: {(int)response.StatusCode} {response.ReasonPhrase}{Environment.NewLine}{xml}");
            }

            var document = XDocument.Parse(xml);
            if (LooksLikeFlexReport(document))
            {
                return xml;
            }

            var status = document.Root?
                .Elements()
                .FirstOrDefault(static element => element.Name.LocalName == "Status")?
                .Value;
            var errorCode = document.Root?
                .Elements()
                .FirstOrDefault(static element => element.Name.LocalName == "ErrorCode")?
                .Value;
            var errorMessage = document.Root?
                .Elements()
                .FirstOrDefault(static element => element.Name.LocalName == "ErrorMessage")?
                .Value;

            if (status == "Fail" && errorCode is not null && PendingErrorCodes.Contains(errorCode) && attempt < options.MaxPollAttempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(options.PollDelaySeconds));
                continue;
            }

            EnsureFlexSuccess(document, "GetStatement");
            throw new InvalidOperationException($"Flex statement did not contain a report payload. Error {errorCode}: {errorMessage}");
        }

        throw new InvalidOperationException("Flex statement polling timed out before the report became available.");
    }

    private static List<FlexPositionSnapshot> ParsePositions(string xml, IReadOnlyCollection<string> symbols)
    {
        var symbolSet = symbols
            .Where(static symbol => !string.IsNullOrWhiteSpace(symbol))
            .Select(static symbol => symbol.Trim().ToUpperInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var document = XDocument.Parse(xml);
        var results = new Dictionary<string, FlexPositionSnapshot>(StringComparer.OrdinalIgnoreCase);

        foreach (var element in document.Descendants())
        {
            var ticker = NormalizeSymbol(
                ReadAttribute(element, "symbol")
                ?? ReadAttribute(element, "underlyingSymbol")
                ?? ReadAttribute(element, "assetSymbol"));

            if (string.IsNullOrWhiteSpace(ticker) || !symbolSet.Contains(ticker))
            {
                continue;
            }

            var price = ReadDecimalAttribute(element, "markPrice")
                ?? ReadDecimalAttribute(element, "price")
                ?? ReadDecimalAttribute(element, "closePrice")
                ?? ReadDecimalAttribute(element, "mtmPrice");

            if (price is null || price <= 0)
            {
                continue;
            }

            var quantity = ReadDecimalAttribute(element, "position")
                ?? ReadDecimalAttribute(element, "quantity")
                ?? 0m;

            results[ticker] = new FlexPositionSnapshot
            {
                Ticker = ticker,
                Name = ReadAttribute(element, "description")
                    ?? ReadAttribute(element, "description1")
                    ?? ticker,
                Currency = ReadAttribute(element, "currency") ?? "USD",
                Quantity = quantity,
                PricePerShare = price.Value
            };
        }

        return results.Values.ToList();
    }

    private static bool LooksLikeFlexReport(XDocument document)
    {
        return document.Root?.Name.LocalName is "FlexQueryResponse" or "FlexStatements";
    }

    private static void EnsureFlexSuccess(XDocument document, string operationName)
    {
        var status = document.Root?
            .Elements()
            .FirstOrDefault(static element => element.Name.LocalName == "Status")?
            .Value;

        if (string.Equals(status, "Success", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var errorCode = document.Root?
            .Elements()
            .FirstOrDefault(static element => element.Name.LocalName == "ErrorCode")?
            .Value;
        var errorMessage = document.Root?
            .Elements()
            .FirstOrDefault(static element => element.Name.LocalName == "ErrorMessage")?
            .Value;

        throw new InvalidOperationException($"{operationName} failed. Error {errorCode}: {errorMessage}");
    }

    private static string? ReadAttribute(XElement element, string attributeName)
    {
        return element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == attributeName)?.Value;
    }

    private static decimal? ReadDecimalAttribute(XElement element, string attributeName)
    {
        var value = ReadAttribute(element, attributeName);
        return decimal.TryParse(value, out var result) ? result : null;
    }

    private static string? NormalizeSymbol(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var token = value
            .Split([' ', '.', ':', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        return string.IsNullOrWhiteSpace(token) ? null : token.ToUpperInvariant();
    }

    private static string AppendTrailingSlash(string value)
    {
        return value.EndsWith('/') ? value : $"{value}/";
    }
}

internal sealed record FlexPositionSnapshot
{
    public string Ticker { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Currency { get; init; } = "USD";

    public decimal Quantity { get; init; }

    public decimal PricePerShare { get; init; }
}

internal sealed record WorkerIngestPayload
{
    [JsonPropertyName("source")]
    public string Source { get; init; } = "IBKR Flex Web Service";

    [JsonPropertyName("notes")]
    public string Notes { get; init; } = string.Empty;

    [JsonPropertyName("asOfDate")]
    public DateTime AsOfDate { get; init; }

    [JsonPropertyName("refreshIntervalMinutes")]
    public int RefreshIntervalMinutes { get; init; }

    [JsonPropertyName("positions")]
    public List<WorkerPositionUpdate> Positions { get; init; } = [];
}

internal sealed record WorkerPositionUpdate
{
    [JsonPropertyName("ticker")]
    public string Ticker { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("currency")]
    public string? Currency { get; init; }

    [JsonPropertyName("quantity")]
    public decimal? Quantity { get; init; }

    [JsonPropertyName("pricePerShare")]
    public decimal? PricePerShare { get; init; }
}
