using System.Net.Http.Json;
using retirement_dashboard.Models;

namespace retirement_dashboard.Services;

public sealed class PlanDataService(HttpClient httpClient)
{
    private const string DataPath = "data/retirement-plan.json";
    private const string RuntimeConfigPath = "data/runtime-config.json";

    public async Task<RetirementDashboardViewModel> LoadAsync()
    {
        var snapshot = await TryLoadLiveSnapshotAsync() ?? await LoadStaticSnapshotAsync();
        return BuildViewModel(snapshot);
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

    private static RetirementDashboardViewModel BuildViewModel(RetirementPlanSnapshot snapshot)
    {
        var usdToTwdRate = snapshot.Assumptions.UsdToTwdRate;
        var targetMonthlyIncomeTwd = snapshot.Goal.TargetMonthlyIncomeTwd;
        var targetAnnualIncomeTwd = targetMonthlyIncomeTwd * 12m;
        var targetCapitalTwd = targetAnnualIncomeTwd / snapshot.Assumptions.AssumedAnnualYieldRate;
        var fixedDepositPrincipalTwd = snapshot.FixedDeposit.PrincipalTwd;
        var fixedDepositAnnualInterestTwd = fixedDepositPrincipalTwd * snapshot.FixedDeposit.AnnualInterestRate;

        var currentInvestedTwd = snapshot.Positions.Sum(position => ConvertToTwd(position.Quantity * position.PricePerShare, position.Currency, usdToTwdRate));
        var currentAssetTwd = snapshot.CurrentCashTwd + currentInvestedTwd + fixedDepositPrincipalTwd;
        var currentAnnualDividendTwd =
            snapshot.Positions.Sum(position => ConvertToTwd(position.Quantity * position.AnnualDividendPerShare, position.Currency, usdToTwdRate)) +
            fixedDepositAnnualInterestTwd;
        var currentMonthlyPassiveIncomeTwd = currentAnnualDividendTwd / 12m;

        var monthsRemaining = Math.Max(0, ((snapshot.Goal.TargetDate.Year - snapshot.AsOfDate.Year) * 12) + snapshot.Goal.TargetDate.Month - snapshot.AsOfDate.Month);
        var annualPlanMonthsRemaining = snapshot.AnnualPlan.Year == snapshot.AsOfDate.Year
            ? Math.Max(1, snapshot.AnnualPlan.Deadline.Month - snapshot.AsOfDate.Month + 1)
            : 0;
        var annualInvestmentTargetTwd = snapshot.AnnualPlan.TargetInvestmentTwd;
        var annualInvestmentGapTwd = Math.Max(0m, annualInvestmentTargetTwd - currentInvestedTwd);
        var requiredMonthlyInvestmentTwd = annualPlanMonthsRemaining == 0 ? annualInvestmentGapTwd : annualInvestmentGapTwd / annualPlanMonthsRemaining;
        var requiredMonthlyInvestmentUsd = usdToTwdRate == 0 ? 0 : requiredMonthlyInvestmentTwd / usdToTwdRate;
        var projectedInvestedAssetsAtTargetTwd = CalculateFutureValue(
            currentInvestedTwd + snapshot.CurrentCashTwd,
            requiredMonthlyInvestmentTwd,
            snapshot.Assumptions.ExpectedAnnualReturnRate,
            monthsRemaining);
        var projectedFixedDepositAtTargetTwd = CalculateFutureValue(
            fixedDepositPrincipalTwd,
            0m,
            snapshot.FixedDeposit.AnnualInterestRate,
            monthsRemaining);
        var projectedPortfolioAtTargetTwd = projectedInvestedAssetsAtTargetTwd + projectedFixedDepositAtTargetTwd;

        var projectedMonthlyIncomeAtTargetTwd = projectedPortfolioAtTargetTwd * snapshot.Assumptions.AssumedAnnualYieldRate / 12m;
        var liveIncomeYieldRate = currentAssetTwd == 0 ? 0 : currentAnnualDividendTwd / currentAssetTwd;
        var liveProjectedMonthlyIncomeAtTargetTwd = projectedPortfolioAtTargetTwd * liveIncomeYieldRate / 12m;
        var capitalGapTwd = Math.Max(0m, targetCapitalTwd - projectedPortfolioAtTargetTwd);
        var monthlyIncomeGapTwd = Math.Max(0m, targetMonthlyIncomeTwd - projectedMonthlyIncomeAtTargetTwd);
        var requiredAdditionalMonthlySavingsTwd = monthsRemaining == 0 ? capitalGapTwd : capitalGapTwd / monthsRemaining;
        var monthlyContributionUsd = requiredMonthlyInvestmentUsd;
        var monthlyContributionTwd = requiredMonthlyInvestmentTwd;

        var positions = snapshot.Positions
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

        var history = snapshot.History
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
        var lastUpdatedText = snapshot.LiveData.LastUpdatedUtc is { } updatedUtc
            ? updatedUtc.ToLocalTime().ToString("yyyy/MM/dd HH:mm")
            : snapshot.AsOfDate.ToString("yyyy/MM/dd");

        return new RetirementDashboardViewModel
        {
            AsOfDate = snapshot.AsOfDate,
            AsOfDateText = snapshot.AsOfDate.ToString("yyyy/MM/dd"),
            TargetDate = snapshot.Goal.TargetDate,
            TargetDateText = snapshot.Goal.TargetDate.ToString("yyyy/MM/dd"),
            IsLiveData = snapshot.LiveData.IsLive,
            DataSourceText = string.IsNullOrWhiteSpace(snapshot.LiveData.Source) ? "手動配置資料" : snapshot.LiveData.Source,
            LastUpdatedText = lastUpdatedText,
            RefreshIntervalMinutes = snapshot.LiveData.RefreshIntervalMinutes,
            RefreshIntervalText = FormatRefreshInterval(snapshot.LiveData.RefreshIntervalMinutes),
            LiveNotes = snapshot.LiveData.Notes,
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
            AnnualPlanYear = snapshot.AnnualPlan.Year,
            AnnualPlanDeadlineText = snapshot.AnnualPlan.Deadline.ToString("yyyy/MM/dd"),
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
            AssumedAnnualYieldText = $"{snapshot.Assumptions.AssumedAnnualYieldRate:P1}",
            ExpectedAnnualReturnText = $"{snapshot.Assumptions.ExpectedAnnualReturnRate:P1}",
            FixedDepositRateText = $"{snapshot.FixedDeposit.AnnualInterestRate:P1}",
            ProgressPercentText = $"{progressPercent:P1}",
            ProgressPercentCss = $"{Math.Min(100m, progressPercent * 100m):0.##}%",
            ProjectedProgressPercentText = $"{projectedProgressPercent:P1}",
            CurrentYieldOnCostText = $"{currentYieldOnCost:P2}",
            Notes = snapshot.Notes,
            Positions = positions,
            History = history
        };
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
}