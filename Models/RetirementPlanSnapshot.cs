namespace retirement_dashboard.Models;

public sealed class RetirementPlanSnapshot
{
    public DateTime AsOfDate { get; set; }

    public RetirementGoal Goal { get; set; } = new();

    public AnnualInvestmentPlan AnnualPlan { get; set; } = new();

    public PortfolioAssumptions Assumptions { get; set; } = new();

    public FixedDepositPlan FixedDeposit { get; set; } = new();

    public LiveDataSettings LiveData { get; set; } = new();

    public decimal CurrentCashTwd { get; set; }

    public string Notes { get; set; } = string.Empty;

    public List<InvestmentPosition> Positions { get; set; } = [];

    public List<ActualHoldingSnapshot> ActualHoldings { get; set; } = [];

    public List<ProgressSnapshot> History { get; set; } = [];
}

public sealed class RetirementGoal
{
    public DateTime TargetDate { get; set; }

    public decimal TargetMonthlyIncomeTwd { get; set; }
}

public sealed class AnnualInvestmentPlan
{
    public int Year { get; set; }

    public decimal TargetInvestmentTwd { get; set; }

    public DateTime Deadline { get; set; }
}

public sealed class PortfolioAssumptions
{
    public decimal AssumedAnnualYieldRate { get; set; }

    public decimal ExpectedAnnualReturnRate { get; set; }

    public decimal UsdToTwdRate { get; set; }

    public decimal MonthlyContributionUsd { get; set; }

    public bool ReinvestDividends { get; set; }
}

public sealed class FixedDepositPlan
{
    public decimal PrincipalTwd { get; set; }

    public decimal AnnualInterestRate { get; set; }

    public bool ReinvestInterest { get; set; }
}

public sealed class LiveDataSettings
{
    public bool IsLive { get; set; }

    public string Source { get; set; } = string.Empty;

    public DateTime? LastUpdatedUtc { get; set; }

    public int RefreshIntervalMinutes { get; set; } = 5;

    public string Notes { get; set; } = string.Empty;
}

public sealed class InvestmentPosition
{
    public string Ticker { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Currency { get; set; } = "USD";

    public decimal Quantity { get; set; }

    public decimal PricePerShare { get; set; }

    public decimal AnnualDividendPerShare { get; set; }

    public string AnnualDividendProxyTicker { get; set; } = string.Empty;

    public decimal MonthlyContributionUsd { get; set; }
}

public sealed class ActualHoldingSnapshot
{
    public string Ticker { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Currency { get; set; } = "TWD";

    public decimal? Quantity { get; set; }

    public decimal? PricePerShare { get; set; }

    public decimal MarketValueTwd { get; set; }

    public string Note { get; set; } = string.Empty;
}

public sealed class ProgressSnapshot
{
    public DateTime Date { get; set; }

    public decimal AssetValueTwd { get; set; }

    public string Note { get; set; } = string.Empty;
}
