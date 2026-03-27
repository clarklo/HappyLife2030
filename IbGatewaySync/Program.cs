using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

var options = await SyncOptions.LoadAsync(Path.Combine(AppContext.BaseDirectory, "appsettings.json"));
var syncer = new IbGatewaySyncRunner(options);
var result = await syncer.RunAsync();

Console.WriteLine(result);

internal sealed record SyncOptions(
    GatewayOptions IbGateway,
    WorkerOptions Worker)
{
    public static async Task<SyncOptions> LoadAsync(string configPath)
    {
        if (!File.Exists(configPath))
        {
            throw new InvalidOperationException($"找不到設定檔：{configPath}");
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
            throw new InvalidOperationException("無法解析 appsettings.json。");
        }

        if (string.IsNullOrWhiteSpace(options.Worker.IngestUrl))
        {
            throw new InvalidOperationException("Worker.IngestUrl 尚未設定。");
        }

        if (string.IsNullOrWhiteSpace(options.Worker.IngestToken))
        {
            throw new InvalidOperationException("Worker.IngestToken 尚未設定。");
        }

        return options;
    }
}

internal sealed record GatewayOptions
{
    public string BaseUrl { get; init; } = "https://localhost:5000/v1/api";

    public string? AccountId { get; init; }

    public string[] Symbols { get; init; } = ["TSM", "CSNDX", "CSPX", "VWRA"];

    public bool SyncQuantities { get; init; }
}

internal sealed record WorkerOptions
{
    public string IngestUrl { get; init; } = string.Empty;

    public string IngestToken { get; init; } = string.Empty;
}

internal sealed class IbGatewaySyncRunner(SyncOptions options)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<string> RunAsync()
    {
        using var gatewayClient = CreateGatewayClient(options.IbGateway.BaseUrl);
        using var workerClient = CreateWorkerClient();

        await TickleAsync(gatewayClient);

        var accountId = await ResolveAccountIdAsync(gatewayClient, options.IbGateway.AccountId);
        var positions = await FetchPositionsAsync(gatewayClient, accountId, options.IbGateway.Symbols);

        if (positions.Count == 0)
        {
            throw new InvalidOperationException("IB Gateway 沒有回傳任何符合設定的持倉。");
        }

        var payload = new WorkerIngestPayload
        {
            Source = "IBKR Client Portal Gateway",
            Notes = $"本機同步工具已從帳號 {accountId} 更新持倉快照。",
            AsOfDate = DateTime.UtcNow,
            RefreshIntervalMinutes = 24 * 60,
            Positions = positions
                .Select(position => new WorkerPositionUpdate
                {
                    Ticker = position.Ticker,
                    Name = position.Name,
                    Currency = position.Currency,
                    Quantity = options.IbGateway.SyncQuantities ? position.Quantity : null,
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
            throw new InvalidOperationException($"Worker ingest 失敗：{(int)response.StatusCode} {response.ReasonPhrase}{Environment.NewLine}{responseBody}");
        }

        return $"同步完成：{accountId}，更新 {positions.Count} 檔標的。";
    }

    private static HttpClient CreateGatewayClient(string baseUrl)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (message, cert, chain, errors) =>
            {
                if (message?.RequestUri?.Host is "localhost" or "127.0.0.1")
                {
                    return true;
                }

                return errors == System.Net.Security.SslPolicyErrors.None;
            }
        };

        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri(AppendTrailingSlash(baseUrl))
        };

        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    private static HttpClient CreateWorkerClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    private static async Task TickleAsync(HttpClient client)
    {
        using var response = await client.PostAsync("tickle", new StringContent("{}", Encoding.UTF8, "application/json"));
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"IB Gateway tickle 失敗：{(int)response.StatusCode} {response.ReasonPhrase}");
        }
    }

    private static async Task<string> ResolveAccountIdAsync(HttpClient client, string? configuredAccountId)
    {
        if (!string.IsNullOrWhiteSpace(configuredAccountId))
        {
            return configuredAccountId;
        }

        using var response = await client.GetAsync("portfolio/accounts");
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"無法取得 IB 帳號清單：{(int)response.StatusCode} {response.ReasonPhrase}");
        }

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("IB 帳號清單格式不符合預期。");
        }

        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var value = item.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            if (item.ValueKind == JsonValueKind.Object)
            {
                var value = ReadString(item, "id")
                    ?? ReadString(item, "accountId")
                    ?? ReadString(item, "accountVan")
                    ?? ReadString(item, "accountIdAlias");

                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        throw new InvalidOperationException("找不到可用的 IB 帳號。");
    }

    private static async Task<List<IbPositionSnapshot>> FetchPositionsAsync(HttpClient client, string accountId, IReadOnlyCollection<string> symbols)
    {
        using var response = await client.GetAsync($"portfolio2/{Uri.EscapeDataString(accountId)}/positions");
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"無法取得 IB 持倉：{(int)response.StatusCode} {response.ReasonPhrase}");
        }

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        var rows = root.ValueKind switch
        {
            JsonValueKind.Array => root.EnumerateArray().ToList(),
            JsonValueKind.Object when root.TryGetProperty("positions", out var nested) && nested.ValueKind == JsonValueKind.Array => nested.EnumerateArray().ToList(),
            _ => []
        };

        var symbolSet = symbols
            .Where(static symbol => !string.IsNullOrWhiteSpace(symbol))
            .Select(static symbol => symbol.Trim().ToUpperInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var results = new List<IbPositionSnapshot>();

        foreach (var row in rows)
        {
            var ticker = NormalizeTicker(row);
            if (string.IsNullOrWhiteSpace(ticker) || !symbolSet.Contains(ticker))
            {
                continue;
            }

            var marketPrice = ReadDecimal(row, "mktPrice")
                ?? ReadDecimal(row, "marketPrice")
                ?? ReadDecimal(row, "price");

            if (marketPrice is null || marketPrice <= 0)
            {
                continue;
            }

            results.Add(new IbPositionSnapshot
            {
                Ticker = ticker,
                Name = ReadString(row, "description")
                    ?? ReadString(row, "name")
                    ?? ticker,
                Currency = ReadString(row, "currency") ?? "USD",
                Quantity = ReadDecimal(row, "position")
                    ?? ReadDecimal(row, "quantity")
                    ?? 0m,
                PricePerShare = marketPrice.Value
            });
        }

        return results;
    }

    private static string? NormalizeTicker(JsonElement row)
    {
        var candidates = new[]
        {
            ReadString(row, "ticker"),
            ReadString(row, "symbol"),
            ReadString(row, "contractDesc"),
            ReadString(row, "contractDescLong"),
            ReadString(row, "displayName")
        };

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var token = candidate
                .Split([' ', '.', ':', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(token))
            {
                return token.ToUpperInvariant();
            }
        }

        return null;
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static decimal? ReadDecimal(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetDecimal(out var number) => number,
            JsonValueKind.String when decimal.TryParse(property.GetString(), out var textNumber) => textNumber,
            _ => null
        };
    }

    private static string AppendTrailingSlash(string value)
    {
        return value.EndsWith("/") ? value : $"{value}/";
    }
}

internal sealed record IbPositionSnapshot
{
    public string Ticker { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Currency { get; init; } = "USD";

    public decimal Quantity { get; init; }

    public decimal PricePerShare { get; init; }
}

internal sealed record WorkerIngestPayload
{
    public string Source { get; init; } = "IBKR Client Portal Gateway";

    public string Notes { get; init; } = string.Empty;

    public DateTime AsOfDate { get; init; }

    public int RefreshIntervalMinutes { get; init; }

    public List<WorkerPositionUpdate> Positions { get; init; } = [];
}

internal sealed record WorkerPositionUpdate
{
    public string Ticker { get; init; } = string.Empty;

    public string? Name { get; init; }

    public string? Currency { get; init; }

    public decimal? Quantity { get; init; }

    public decimal? PricePerShare { get; init; }
}
