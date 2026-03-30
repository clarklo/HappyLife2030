using System.Net.Http.Json;
using retirement_dashboard.Models;

namespace retirement_dashboard.Services;

public sealed class PlanDataService(HttpClient httpClient)
{
    private const string DataPath = "data/retirement-plan.json";
    private const string RuntimeConfigPath = "data/runtime-config.json";

    public async Task<RetirementDashboardViewModel> LoadAsync()
    {
        var staticSnapshot = await LoadStaticSnapshotAsync();
        var liveSnapshot = await TryLoadLiveSnapshotAsync();
        return BuildViewModel(staticSnapshot, liveSnapshot);
    }

    private async Task<RetirementPlanSnapshot?> TryLoadLiveSnapshotAsync()
    {
        var runtimeConfig = await LoadRuntimeConfigAsync();
        if (string.IsNullOrWhiteSpace(runtimeConfig.LiveApiUrl))
        {
            return null;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, AppendCacheBuster(runtimeConfig.LiveApiUrl));
            request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true, NoStore = true };

            using var response = await httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<RetirementPlanSnapshot>();
        }
        catch
        {
            return null;
        }
    }

    private async Task<RetirementPlanSnapshot> LoadStaticSnapshotAsync()
    {
        var snapshot = await httpClient.GetFromJsonAsync<RetirementPlanSnapshot>(AppendCacheBuster(DataPath));

        if (snapshot is null)
        {
            throw new InvalidOperationException("目前無法讀取退休規劃資料。請稍後再試。");
        }

        return snapshot;
    }

    private async Task<RuntimeConfig> LoadRuntimeConfigAsync()
    {
        try
        {
            var config = await httpClient.GetFromJsonAsync<RuntimeConfig>(AppendCacheBuster(RuntimeConfigPath));
            return config ?? new RuntimeConfig();
        }
        catch
        {
            return new RuntimeConfig();
        }
    }

    private static string AppendCacheBuster(string path)
    {
        var separator = path.Contains('?') ? '&' : '?';
        return $"{path}{separator}v={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
    }

    private static RetirementDashboardViewModel BuildViewModel(RetirementPlanSnapshot staticSnapshot, RetirementPlanSnapshot? liveSnapshot)
    {
        var scenarioSnapshot = BuildScenarioSnapshot(staticSnapshot, liveSnapshot);
        var displaySnapshot = liveSnapshot ?? scenarioSnapshot;
        var usdToTwdRate = scenarioSnapshot.Assumptions.UsdToTwdRate;
        var targetMonthlyIncomeTwd = scenarioSnapshot.Goal.TargetMonthlyIncomeTwd;
        var targetAnnualIncomeTwd = targetMonthlyIncomeTwd * 12m;
        var targetCapitalTwd = targetAnnualIncomeTwd / scenarioSnapshot.Assumptions.AssumedAnnualYieldRate;
        var fixedDepositPrincipalTwd = scenarioSnapshot.FixedDeposit.PrincipalTwd;
        var fixedDepositAnnualInterestTwd = fixedDepositPrincipalTwd * scenarioSnapshot.FixedDeposit.AnnualInterestRate;

        var currentInvestedTwd = scenarioSnapshot.Positions.Sum(position => ConvertToTwd(position.Quantity * position.PricePerShare, position.Currency, usdToTwdRate));
        var currentAssetTwd = scenarioSnapshot.CurrentCashTwd + currentInvestedTwd + fixedDepositPrincipalTwd;
        var currentAnnualDividendTwd =
            scenarioSnapshot.Positions.Sum(position => ConvertToTwd(position.Quantity * position.AnnualDividendPerShare, position.Currency, usdToTwdRate)) +
            fixedDepositAnnualInterestTwd;
        var currentMonthlyPassiveIncomeTwd = currentAnnualDividendTwd / 12m;

        var monthsRemaining = Math.Max(0, ((scenarioSnapshot.Goal.TargetDate.Year - displaySnapshot.AsOfDate.Year) * 12) + scenarioSnapshot.Goal.TargetDate.Month - displaySnapshot.AsOfDate.Month);
        var annualPlanMonthsRemaining = scenarioSnapshot.AnnualPlan.Year == displaySnapshot.AsOfDate.Year
            ? Math.Max(1, scenarioSnapshot.AnnualPlan.Deadline.Month - displaySnapshot.AsOfDate.Month + 1)
            : 0;
        var annualInvestmentTargetTwd = scenarioSnapshot.AnnualPlan.TargetInvestmentTwd;
        var annualInvestmentGapTwd = Math.Max(0m, annualInvestmentTargetTwd - currentInvestedTwd);
        var requiredMonthlyInvestmentTwd = annualPlanMonthsRemaining == 0 ? annualInvestmentGapTwd : annualInvestmentGapTwd / annualPlanMonthsRemaining;
        var requiredMonthlyInvestmentUsd = usdToTwdRate == 0 ? 0 : requiredMonthlyInvestmentTwd / usdToTwdRate;
        var projectedInvestedAssetsAtTargetTwd = CalculateFutureValue(
            currentInvestedTwd + scenarioSnapshot.CurrentCashTwd,
            requiredMonthlyInvestmentTwd,
            scenarioSnapshot.Assumptions.ExpectedAnnualReturnRate,
            monthsRemaining);
        var projectedFixedDepositAtTargetTwd = CalculateFutureValue(
            fixedDepositPrincipalTwd,
            0m,
            scenarioSnapshot.FixedDeposit.AnnualInterestRate,
            monthsRemaining);
        var projectedPortfolioAtTargetTwd = projectedInvestedAssetsAtTargetTwd + projectedFixedDepositAtTargetTwd;

        var projectedMonthlyIncomeAtTargetTwd = projectedPortfolioAtTargetTwd * scenarioSnapshot.Assumptions.AssumedAnnualYieldRate / 12m;
        var liveIncomeYieldRate = currentAssetTwd == 0 ? 0 : currentAnnualDividendTwd / currentAssetTwd;
        var liveProjectedMonthlyIncomeAtTargetTwd = projectedPortfolioAtTargetTwd * liveIncomeYieldRate / 12m;
        var capitalGapTwd = Math.Max(0m, targetCapitalTwd - projectedPortfolioAtTargetTwd);
        var monthlyIncomeGapTwd = Math.Max(0m, targetMonthlyIncomeTwd - projectedMonthlyIncomeAtTargetTwd);
        var requiredAdditionalMonthlySavingsTwd = monthsRemaining == 0 ? capitalGapTwd : capitalGapTwd / monthsRemaining;
        var monthlyContributionUsd = requiredMonthlyInvestmentUsd;
        var monthlyContributionTwd = requiredMonthlyInvestmentTwd;

        var positions = scenarioSnapshot.Positions
            .Select(position =>
            {
                var marketValueTwd = ConvertToTwd(position.Quantity * position.PricePerShare, position.Currency, usdToTwdRate);
                var annualDividendTwd = ConvertToTwd(position.Quantity * position.AnnualDividendPerShare, position.Currency, usdToTwdRate);
                var allocationShare = currentAssetTwd == 0 ? 0 : marketValueTwd / currentAssetTwd;
                var annualDividendProxyTicker = ResolveAnnualDividendProxyTicker(position);
                var annualDividendSuffix = string.IsNullOrWhiteSpace(annualDividendProxyTicker)
                    ? string.Empty
                    : $" ({annualDividendProxyTicker})";

                return new PositionViewModel
                {
                    Ticker = position.Ticker,
                    Name = position.Name,
                    QuantityText = position.Quantity.ToString("N2"),
                    PriceText = position.Currency == "USD" ? $"US${position.PricePerShare:N2}" : $"NT${position.PricePerShare:N2}",
                    MarketValueText = $"NT${marketValueTwd:N0}",
                    AnnualDividendText = $"NT${annualDividendTwd:N0}{annualDividendSuffix}",
                    PlannedContributionUsdText = $"US${position.MonthlyContributionUsd:N0}",
                    AllocationShareText = $"{allocationShare:P0}",
                    AllocationShareCss = $"{allocationShare * 100m:0.##}%"
                };
            })
            .ToList();

        var actualHoldingDisplays = BuildActualHoldingDisplays(staticSnapshot, liveSnapshot, usdToTwdRate);
        var actualHoldings = actualHoldingDisplays
            .Select(holding => new ActualHoldingViewModel
            {
                Ticker = holding.Ticker,
                Name = holding.Name,
                MarketValueText = $"NT${holding.MarketValueTwd:N0}",
                DetailText = holding.DetailText
            })
            .ToList();
        var actualHoldingsValueTwd = actualHoldingDisplays.Sum(holding => holding.MarketValueTwd);

        var history = scenarioSnapshot.History
            .OrderByDescending(point => point.Date)
            .Select(point =>
            {
                var progress = targetCapitalTwd == 0 ? 0 : point.AssetValueTwd / targetCapitalTwd;
                return new HistoryPointViewModel
                {
                    DateText = point.Date.ToString("yyyy/MM/dd"),
                    AssetValueText = $"NT${point.AssetValueTwd:N0}",
                    ProgressText = $"{progress:P1} 達成率",
                    Note = point.Note
                };
            })
            .ToList();

        var progressPercent = targetCapitalTwd == 0 ? 0 : currentAssetTwd / targetCapitalTwd;
        var projectedProgressPercent = targetCapitalTwd == 0 ? 0 : projectedPortfolioAtTargetTwd / targetCapitalTwd;
        var currentYieldOnCost = currentAssetTwd == 0 ? 0 : currentAnnualDividendTwd / currentAssetTwd;
        var lastUpdatedText = displaySnapshot.LiveData.LastUpdatedUtc is { } updatedUtc
            ? updatedUtc.ToLocalTime().ToString("yyyy/MM/dd HH:mm")
            : displaySnapshot.AsOfDate.ToString("yyyy/MM/dd");
        var liveNotes = BuildLiveNotes(displaySnapshot.LiveData.Notes);
        var actualHoldingsSourceText = liveSnapshot is null
            ? "2330 依你手動提供約值；海外 ETF 暫時顯示本地快照。"
            : "2330 依你手動提供約值；海外 ETF 依最新 IB / Worker 部位更新。";

        return new RetirementDashboardViewModel
        {
            AsOfDate = displaySnapshot.AsOfDate,
            AsOfDateText = displaySnapshot.AsOfDate.ToString("yyyy/MM/dd"),
            TargetDate = scenarioSnapshot.Goal.TargetDate,
            TargetDateText = scenarioSnapshot.Goal.TargetDate.ToString("yyyy/MM/dd"),
            IsLiveData = displaySnapshot.LiveData.IsLive,
            DataSourceText = string.IsNullOrWhiteSpace(displaySnapshot.LiveData.Source) ? "手動配置資料" : displaySnapshot.LiveData.Source,
            LastUpdatedText = lastUpdatedText,
            RefreshIntervalMinutes = displaySnapshot.LiveData.RefreshIntervalMinutes,
            RefreshIntervalText = FormatRefreshInterval(displaySnapshot.LiveData.RefreshIntervalMinutes),
            LiveNotes = liveNotes,
            ActualHoldingsValueTwd = actualHoldingsValueTwd,
            ActualHoldingsValueText = $"NT${actualHoldingsValueTwd:N0}",
            ActualHoldingsCount = actualHoldings.Count,
            ActualHoldingsSourceText = actualHoldingsSourceText,
            CurrentAssetTwd = currentAssetTwd,
            CurrentInvestedTwd = currentInvestedTwd,
            FixedDepositPrincipalTwd = fixedDepositPrincipalTwd,
            FixedDepositAnnualInterestTwd = fixedDepositAnnualInterestTwd,
            ProjectedFixedDepositAtTargetTwd = projectedFixedDepositAtTargetTwd,
            CurrentAnnualDividendTwd = currentAnnualDividendTwd,
            CurrentMonthlyPassiveIncomeTwd = currentMonthlyPassiveIncomeTwd,
            LiveIncomeYieldRate = liveIncomeYieldRate,
            LiveIncomeYieldRateText = $"{liveIncomeYieldRate:P2}",
            LiveProjectedMonthlyIncomeAtTargetTwd = liveProjectedMonthlyIncomeAtTargetTwd,
            AnnualPlanYear = scenarioSnapshot.AnnualPlan.Year,
            AnnualPlanDeadlineText = scenarioSnapshot.AnnualPlan.Deadline.ToString("yyyy/MM/dd"),
            AnnualPlanMonthsRemaining = annualPlanMonthsRemaining,
            AnnualInvestmentTargetTwd = annualInvestmentTargetTwd,
            AnnualInvestmentGapTwd = annualInvestmentGapTwd,
            RequiredMonthlyInvestmentTwd = requiredMonthlyInvestmentTwd,
            RequiredMonthlyInvestmentUsd = requiredMonthlyInvestmentUsd,
            AnnualPlanProgressText = annualInvestmentTargetTwd == 0 ? "0.0%" : $"{currentInvestedTwd / annualInvestmentTargetTwd:P1}",
            AnnualPlanProgressCss = annualInvestmentTargetTwd == 0 ? "0%" : $"{Math.Min(100m, currentInvestedTwd / annualInvestmentTargetTwd * 100m):0.##}%",
            AnnualPlanIsOnTrack = annualInvestmentGapTwd == 0,
            TargetMonthlyIncomeTwd = targetMonthlyIncomeTwd,
            TargetAnnualIncomeTwd = targetAnnualIncomeTwd,
            TargetCapitalTwd = targetCapitalTwd,
            CapitalGapTwd = capitalGapTwd,
            MonthlyContributionUsd = monthlyContributionUsd,
            MonthlyContributionTwd = monthlyContributionTwd,
            MonthsRemaining = monthsRemaining,
            ProjectedPortfolioAtTargetTwd = projectedPortfolioAtTargetTwd,
            ProjectedMonthlyIncomeAtTargetTwd = projectedMonthlyIncomeAtTargetTwd,
            MonthlyIncomeGapTwd = monthlyIncomeGapTwd,
            RequiredAdditionalMonthlySavingsTwd = requiredAdditionalMonthlySavingsTwd,
            AssumedAnnualYieldText = $"{scenarioSnapshot.Assumptions.AssumedAnnualYieldRate:P1}",
            ExpectedAnnualReturnText = $"{scenarioSnapshot.Assumptions.ExpectedAnnualReturnRate:P1}",
            FixedDepositRateText = $"{scenarioSnapshot.FixedDeposit.AnnualInterestRate:P1}",
            ProgressPercentText = $"{progressPercent:P1}",
            ProgressPercentCss = $"{Math.Min(100m, progressPercent * 100m):0.##}%",
            ProjectedProgressPercentText = $"{projectedProgressPercent:P1}",
            CurrentYieldOnCostText = $"{currentYieldOnCost:P2}",
            Notes = scenarioSnapshot.Notes,
            ActualHoldings = actualHoldings,
            Positions = positions,
            History = history
        };
    }

    private static RetirementPlanSnapshot BuildScenarioSnapshot(RetirementPlanSnapshot staticSnapshot, RetirementPlanSnapshot? liveSnapshot)
    {
        var liveLookup = (liveSnapshot?.Positions ?? [])
            .GroupBy(position => position.Ticker, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        return new RetirementPlanSnapshot
        {
            AsOfDate = liveSnapshot?.AsOfDate ?? staticSnapshot.AsOfDate,
            Goal = staticSnapshot.Goal,
            AnnualPlan = staticSnapshot.AnnualPlan,
            Assumptions = staticSnapshot.Assumptions,
            FixedDeposit = staticSnapshot.FixedDeposit,
            LiveData = liveSnapshot?.LiveData ?? staticSnapshot.LiveData,
            CurrentCashTwd = staticSnapshot.CurrentCashTwd,
            Notes = staticSnapshot.Notes,
            Positions = staticSnapshot.Positions
                .Select(position =>
                {
                    liveLookup.TryGetValue(position.Ticker, out var livePosition);

                    return new InvestmentPosition
                    {
                        Ticker = position.Ticker,
                        Name = position.Name,
                        Currency = position.Currency,
                        Quantity = position.Quantity,
                        PricePerShare = livePosition?.PricePerShare ?? position.PricePerShare,
                        AnnualDividendPerShare = position.AnnualDividendPerShare,
                        AnnualDividendProxyTicker = position.AnnualDividendProxyTicker,
                        MonthlyContributionUsd = position.MonthlyContributionUsd
                    };
                })
                .ToList(),
            ActualHoldings = staticSnapshot.ActualHoldings,
            History = staticSnapshot.History
        };
    }

    private static List<ActualHoldingDisplay> BuildActualHoldingDisplays(RetirementPlanSnapshot staticSnapshot, RetirementPlanSnapshot? liveSnapshot, decimal usdToTwdRate)
    {
        var liveLookup = (liveSnapshot?.Positions ?? [])
            .GroupBy(position => position.Ticker, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var displays = new List<ActualHoldingDisplay>();

        foreach (var holding in staticSnapshot.ActualHoldings)
        {
            if (liveLookup.TryGetValue(holding.Ticker, out var livePosition))
            {
                var marketValueTwd = ConvertToTwd(livePosition.Quantity * livePosition.PricePerShare, livePosition.Currency, usdToTwdRate);
                displays.Add(new ActualHoldingDisplay
                {
                    Ticker = holding.Ticker,
                    Name = holding.Name,
                    MarketValueTwd = marketValueTwd,
                    DetailText = BuildActualHoldingDetailText(livePosition.Quantity, livePosition.PricePerShare, livePosition.Currency, holding.Note, marketValueTwd)
                });

                continue;
            }

            displays.Add(new ActualHoldingDisplay
            {
                Ticker = holding.Ticker,
                Name = holding.Name,
                MarketValueTwd = holding.MarketValueTwd,
                DetailText = BuildActualHoldingDetailText(holding.Quantity, holding.PricePerShare, holding.Currency, holding.Note, holding.MarketValueTwd)
            });
        }

        return displays;
    }

    private static string BuildActualHoldingDetailText(decimal? quantity, decimal? pricePerShare, string currency, string note, decimal marketValueTwd)
    {
        if (quantity is > 0 && pricePerShare is > 0)
        {
            var priceText = currency == "USD" ? $"US${pricePerShare.Value:N2}" : $"NT${pricePerShare.Value:N2}";
            return $"{quantity.Value:N2} 股 / {priceText}";
        }

        if (!string.IsNullOrWhiteSpace(note))
        {
            return note;
        }

        return $"市值約 NT${marketValueTwd:N0}";
    }

    private static string BuildLiveNotes(string liveNotes)
    {
        const string simulationNote = "退休情境卡片已改用模擬年現金股利計算；真實持股另列於最上方。";

        if (string.IsNullOrWhiteSpace(liveNotes))
        {
            return simulationNote;
        }

        return $"{liveNotes} {simulationNote}";
    }

    private static decimal CalculateFutureValue(decimal presentValue, decimal monthlyContribution, decimal annualReturnRate, int months)
    {
        if (months <= 0)
        {
            return presentValue;
        }

        var monthlyRate = annualReturnRate / 12m;

        if (monthlyRate == 0)
        {
            return presentValue + (monthlyContribution * months);
        }

        var growthFactor = (decimal)Math.Pow((double)(1 + monthlyRate), months);
        var futureValueOfPrincipal = presentValue * growthFactor;
        var futureValueOfContributions = monthlyContribution * ((growthFactor - 1) / monthlyRate);

        return futureValueOfPrincipal + futureValueOfContributions;
    }

    private static string ResolveAnnualDividendProxyTicker(InvestmentPosition position)
    {
        if (!string.IsNullOrWhiteSpace(position.AnnualDividendProxyTicker))
        {
            return position.AnnualDividendProxyTicker;
        }

        return position.Ticker.ToUpperInvariant() switch
        {
            "CSNDX" => "EXXT",
            "CSPX" => "IUSA",
            "VWRA" => "VWRL",
            _ => string.Empty
        };
    }

    private static decimal ConvertToTwd(decimal amount, string currency, decimal usdToTwdRate)
    {
        return currency == "USD" ? amount * usdToTwdRate : amount;
    }

    private static string FormatRefreshInterval(int refreshIntervalMinutes)
    {
        if (refreshIntervalMinutes > 0 && refreshIntervalMinutes % 60 == 0)
        {
            var hours = refreshIntervalMinutes / 60;
            return $"每 {hours} 小時更新一次";
        }

        return $"每 {refreshIntervalMinutes} 分鐘更新一次";
    }

    private sealed class RuntimeConfig
    {
        public string LiveApiUrl { get; set; } = string.Empty;
    }

    private sealed class ActualHoldingDisplay
    {
        public string Ticker { get; init; } = string.Empty;

        public string Name { get; init; } = string.Empty;

        public decimal MarketValueTwd { get; init; }

        public string DetailText { get; init; } = string.Empty;
    }
}
