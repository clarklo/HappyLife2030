using System.Net.Http.Json;
using retirement_dashboard.Models;

namespace retirement_dashboard.Services;

public sealed class PlanDataService(HttpClient httpClient)
{
    private const string DataPath = "data/retirement-plan.json";

    public async Task<RetirementDashboardViewModel> LoadAsync()
    {
        var snapshot = await httpClient.GetFromJsonAsync<RetirementPlanSnapshot>(DataPath);

        if (snapshot is null)
        {
            throw new InvalidOperationException("找不到退休計劃資料檔。");
        }

        return BuildViewModel(snapshot);
    }

    private static RetirementDashboardViewModel BuildViewModel(RetirementPlanSnapshot snapshot)
    {
        var usdToTwdRate = snapshot.Assumptions.UsdToTwdRate;
        var targetMonthlyIncomeTwd = snapshot.Goal.TargetMonthlyIncomeTwd;
        var targetAnnualIncomeTwd = targetMonthlyIncomeTwd * 12m;
        var targetCapitalTwd = targetAnnualIncomeTwd / snapshot.Assumptions.AssumedAnnualYieldRate;

        var currentAssetTwd = snapshot.CurrentCashTwd + snapshot.Positions.Sum(position => ConvertToTwd(position.Quantity * position.PricePerShare, position.Currency, usdToTwdRate));
        var currentAnnualDividendTwd = snapshot.Positions.Sum(position => ConvertToTwd(position.Quantity * position.AnnualDividendPerShare, position.Currency, usdToTwdRate));

        var monthsRemaining = Math.Max(0, ((snapshot.Goal.TargetDate.Year - snapshot.AsOfDate.Year) * 12) + snapshot.Goal.TargetDate.Month - snapshot.AsOfDate.Month);
        var projectedPortfolioAtTargetTwd = CalculateFutureValue(
            currentAssetTwd,
            snapshot.Assumptions.MonthlyContributionUsd * usdToTwdRate,
            snapshot.Assumptions.ExpectedAnnualReturnRate,
            monthsRemaining);

        var projectedMonthlyIncomeAtTargetTwd = projectedPortfolioAtTargetTwd * snapshot.Assumptions.AssumedAnnualYieldRate / 12m;
        var capitalGapTwd = Math.Max(0m, targetCapitalTwd - projectedPortfolioAtTargetTwd);
        var monthlyIncomeGapTwd = Math.Max(0m, targetMonthlyIncomeTwd - projectedMonthlyIncomeAtTargetTwd);
        var requiredAdditionalMonthlySavingsTwd = monthsRemaining == 0 ? capitalGapTwd : capitalGapTwd / monthsRemaining;
        var monthlyContributionUsd = snapshot.Assumptions.MonthlyContributionUsd;
        var monthlyContributionTwd = monthlyContributionUsd * usdToTwdRate;

        var positions = snapshot.Positions
            .Select(position =>
            {
                var marketValueTwd = ConvertToTwd(position.Quantity * position.PricePerShare, position.Currency, usdToTwdRate);
                var annualDividendTwd = ConvertToTwd(position.Quantity * position.AnnualDividendPerShare, position.Currency, usdToTwdRate);
                var allocationShare = currentAssetTwd == 0 ? 0 : marketValueTwd / currentAssetTwd;

                return new PositionViewModel
                {
                    Ticker = position.Ticker,
                    Name = position.Name,
                    QuantityText = position.Quantity.ToString("N2"),
                    PriceText = position.Currency == "USD" ? $"US${position.PricePerShare:N2}" : $"NT${position.PricePerShare:N2}",
                    MarketValueText = $"NT${marketValueTwd:N0}",
                    AnnualDividendText = $"NT${annualDividendTwd:N0}",
                    PlannedContributionUsdText = $"US${position.MonthlyContributionUsd:N0}",
                    AllocationShareText = $"{allocationShare:P0}",
                    AllocationShareCss = $"{allocationShare * 100m:0.##}%"
                };
            })
            .ToList();

        var history = snapshot.History
            .OrderBy(point => point.Date)
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

        return new RetirementDashboardViewModel
        {
            AsOfDate = snapshot.AsOfDate,
            AsOfDateText = snapshot.AsOfDate.ToString("yyyy/MM/dd"),
            TargetDate = snapshot.Goal.TargetDate,
            TargetDateText = snapshot.Goal.TargetDate.ToString("yyyy/MM/dd"),
            CurrentAssetTwd = currentAssetTwd,
            CurrentAnnualDividendTwd = currentAnnualDividendTwd,
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

    private static decimal ConvertToTwd(decimal amount, string currency, decimal usdToTwdRate)
    {
        return currency == "USD" ? amount * usdToTwdRate : amount;
    }
}
