namespace retirement_dashboard.Models;

public sealed class RetirementPlanSnapshot
{
    public DateTime AsOfDate { get; set; }

    public RetirementGoal Goal { get; set; } = new();

    public PortfolioAssumptions Assumptions { get; set; } = new();

    public decimal CurrentCashTwd { get; set; }

    public string Notes { get; set; } = string.Empty;

    public List<InvestmentPosition> Positions { get; set; } = [];

    public List<ProgressSnapshot> History { get; set; } = [];
}

public sealed class RetirementGoal
{
    public DateTime TargetDate { get; set; }

    public decimal TargetMonthlyIncomeTwd { get; set; }
}

public sealed class PortfolioAssumptions
{
    public decimal AssumedAnnualYieldRate { get; set; }

    public decimal ExpectedAnnualReturnRate { get; set; }

    public decimal UsdToTwdRate { get; set; }

    public decimal MonthlyContributionUsd { get; set; }

    public bool ReinvestDividends { get; set; }
}

public sealed class InvestmentPosition
{
    public string Ticker { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Currency { get; set; } = "USD";

    public decimal Quantity { get; set; }

    public decimal PricePerShare { get; set; }

    public decimal AnnualDividendPerShare { get; set; }

    public decimal MonthlyContributionUsd { get; set; }
}

public sealed class ProgressSnapshot
{
    public DateTime Date { get; set; }

    public decimal AssetValueTwd { get; set; }

    public string Note { get; set; } = string.Empty;
}
