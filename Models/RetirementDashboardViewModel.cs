namespace retirement_dashboard.Models;

public sealed class RetirementDashboardViewModel
{
    public DateTime AsOfDate { get; init; }

    public string AsOfDateText { get; init; } = string.Empty;

    public DateTime TargetDate { get; init; }

    public string TargetDateText { get; init; } = string.Empty;

    public bool IsLiveData { get; init; }

    public string DataSourceText { get; init; } = string.Empty;

    public string LastUpdatedText { get; init; } = string.Empty;

    public int RefreshIntervalMinutes { get; init; }

    public string RefreshIntervalText { get; init; } = string.Empty;

    public string LiveNotes { get; init; } = string.Empty;

    public decimal ActualHoldingsValueTwd { get; init; }

    public string ActualHoldingsValueText { get; init; } = string.Empty;

    public int ActualHoldingsCount { get; init; }

    public string ActualHoldingsSourceText { get; init; } = string.Empty;

    public int WeeklyBuyTuesdayCount { get; init; }

    public string WeeklyBuyPlanWindowText { get; init; } = string.Empty;

    public decimal WeeklyBuyTotalTwd { get; init; }

    public string WeeklyBuyTotalText { get; init; } = string.Empty;

    public decimal CurrentAssetTwd { get; init; }

    public decimal CurrentInvestedTwd { get; init; }

    public decimal FixedDepositPrincipalTwd { get; init; }

    public decimal FixedDepositAnnualInterestTwd { get; init; }

    public decimal ProjectedFixedDepositAtTargetTwd { get; init; }

    public decimal CurrentAnnualDividendTwd { get; init; }

    public decimal CurrentMonthlyPassiveIncomeTwd { get; init; }

    public decimal LiveIncomeYieldRate { get; init; }

    public string LiveIncomeYieldRateText { get; init; } = string.Empty;

    public decimal LiveProjectedMonthlyIncomeAtTargetTwd { get; init; }

    public decimal TargetMonthlyIncomeTwd { get; init; }

    public decimal TargetAnnualIncomeTwd { get; init; }

    public decimal TargetCapitalTwd { get; init; }

    public decimal CapitalGapTwd { get; init; }

    public decimal MonthlyContributionUsd { get; init; }

    public decimal MonthlyContributionTwd { get; init; }

    public int MonthsRemaining { get; init; }

    public int AnnualPlanMonthsRemaining { get; init; }

    public int AnnualPlanYear { get; init; }

    public string AnnualPlanDeadlineText { get; init; } = string.Empty;

    public decimal AnnualInvestmentTargetTwd { get; init; }

    public decimal AnnualInvestmentGapTwd { get; init; }

    public decimal RequiredMonthlyInvestmentTwd { get; init; }

    public decimal RequiredMonthlyInvestmentUsd { get; init; }

    public string AnnualPlanProgressText { get; init; } = string.Empty;

    public string AnnualPlanProgressCss { get; init; } = string.Empty;

    public bool AnnualPlanIsOnTrack { get; init; }

    public decimal ProjectedPortfolioAtTargetTwd { get; init; }

    public decimal ProjectedMonthlyIncomeAtTargetTwd { get; init; }

    public decimal MonthlyIncomeGapTwd { get; init; }

    public decimal RequiredAdditionalMonthlySavingsTwd { get; init; }

    public string AssumedAnnualYieldText { get; init; } = string.Empty;

    public string ExpectedAnnualReturnText { get; init; } = string.Empty;

    public string FixedDepositRateText { get; init; } = string.Empty;

    public string ProgressPercentText { get; init; } = string.Empty;

    public string ProgressPercentCss { get; init; } = string.Empty;

    public string ProjectedProgressPercentText { get; init; } = string.Empty;

    public string CurrentYieldOnCostText { get; init; } = string.Empty;

    public string Notes { get; init; } = string.Empty;

    public IReadOnlyList<ActualHoldingViewModel> ActualHoldings { get; init; } = [];

    public IReadOnlyList<WeeklyBuyPlanViewModel> WeeklyBuyPlans { get; init; } = [];

    public IReadOnlyList<PositionViewModel> Positions { get; init; } = [];

    public IReadOnlyList<HistoryPointViewModel> History { get; init; } = [];
}

public sealed class ActualHoldingViewModel
{
    public string Ticker { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string MarketValueText { get; init; } = string.Empty;

    public string DetailText { get; init; } = string.Empty;
}

public sealed class WeeklyBuyPlanViewModel
{
    public string Ticker { get; init; } = string.Empty;

    public string CurrentHoldingTicker { get; init; } = string.Empty;

    public string TargetValueText { get; init; } = string.Empty;

    public string CurrentValueText { get; init; } = string.Empty;

    public string GapValueText { get; init; } = string.Empty;

    public string WeeklyBuyText { get; init; } = string.Empty;

    public string WeeklyBuyQuantityText { get; init; } = string.Empty;

    public string NoteText { get; init; } = string.Empty;
}

public sealed class PositionViewModel
{
    public string Ticker { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string QuantityText { get; init; } = string.Empty;

    public string PriceText { get; init; } = string.Empty;

    public string MarketValueText { get; init; } = string.Empty;

    public string AnnualDividendText { get; init; } = string.Empty;

    public string PlannedContributionUsdText { get; init; } = string.Empty;

    public string AllocationShareText { get; init; } = string.Empty;

    public string AllocationShareCss { get; init; } = string.Empty;
}

public sealed class HistoryPointViewModel
{
    public string DateText { get; init; } = string.Empty;

    public string AssetValueText { get; init; } = string.Empty;

    public string ProgressText { get; init; } = string.Empty;

    public string Note { get; init; } = string.Empty;
}
