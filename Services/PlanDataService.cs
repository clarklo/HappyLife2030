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

        var today = DateTime.Today;
        var annualPlanDeadline = scenarioSnapshot.AnnualPlan.Deadline.Date;
        var monthsRemaining = Math.Max(0, ((scenarioSnapshot.Goal.TargetDate.Year - today.Year) * 12) + scenarioSnapshot.Goal.TargetDate.Month - today.Month);
        var annualPlanMonthsRemaining = scenarioSnapshot.AnnualPlan.Year == today.Year
            ? Math.Max(1, annualPlanDeadline.Month - today.Month + 1)
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

        var actualHoldingDisplays = BuildActualHoldingDisplays(staticSnapshot, liveSnapshot, scenarioSnapshot.Positions, usdToTwdRate);
        var actualHoldings = actualHoldingDisplays
            .Select(holding => new ActualHoldingViewModel
            {
                Ticker = holding.Ticker,
                Name = holding.Name,
                MarketValueText = $"NT${holding.MarketValueTwd:N0}",
                DetailText = holding.DetailText
            })
            .ToList();
        var actualDividendBreakdowns = actualHoldingDisplays
            .Select(holding => new ActualDividendBreakdownViewModel
            {
                Ticker = holding.Ticker,
                Name = holding.Name,
                MonthlyDividendText = $"NT${(holding.AnnualDividendTwd / 12m):N0}",
                AnnualDividendText = $"年股利 NT${holding.AnnualDividendTwd:N0}",
                NoteText = holding.DividendNote
            })
            .ToList();
        var actualHoldingsValueTwd = actualHoldingDisplays.Sum(holding => holding.MarketValueTwd);
        var actualDividendAnnualTwd = actualHoldingDisplays.Sum(holding => holding.AnnualDividendTwd);
        var actualDividendMonthlyTwd = actualDividendAnnualTwd / 12m;
        var actualDividendYieldRate = actualHoldingsValueTwd == 0 ? 0 : actualDividendAnnualTwd / actualHoldingsValueTwd;
        var actualDividendProgress = targetMonthlyIncomeTwd == 0 ? 0 : actualDividendMonthlyTwd / targetMonthlyIncomeTwd;
        var actualDividendPieProgress = Math.Min(1m, actualDividendProgress);
        var actualDividendPieDegrees = actualDividendPieProgress * 360m;
        var actualDividendTargetGapTwd = Math.Max(0m, targetMonthlyIncomeTwd - actualDividendMonthlyTwd);
        var weeklyBuyTuesdayCount = CountRemainingWeekdayOccurrences(today, annualPlanDeadline, DayOfWeek.Tuesday);
        var weeklyBuyPlans = BuildWeeklyBuyPlans(actualHoldingDisplays, scenarioSnapshot.Positions, weeklyBuyTuesdayCount, usdToTwdRate);
        var weeklyBuyTotalTwd = weeklyBuyPlans.Sum(plan => plan.WeeklyBuyTwd);

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
        var weeklyBuyPlanWindowText = weeklyBuyTuesdayCount == 0
            ? $"{today:yyyy/MM/dd} 之後已沒有可用星期二。"
            : $"從 {today:yyyy/MM/dd} 到 {annualPlanDeadline:yyyy/MM/dd}，還有 {weeklyBuyTuesdayCount} 個星期二可平均分攤。";

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
            ActualDividendAnnualTwd = actualDividendAnnualTwd,
            ActualDividendAnnualText = $"NT${actualDividendAnnualTwd:N0}",
            ActualDividendMonthlyTwd = actualDividendMonthlyTwd,
            ActualDividendMonthlyText = $"NT${actualDividendMonthlyTwd:N0}",
            ActualDividendYieldText = $"{actualDividendYieldRate:P2}",
            ActualDividendProgressText = $"{actualDividendProgress:P1}",
            ActualDividendProgressCss = $"{Math.Min(100m, actualDividendProgress * 100m):0.##}%",
            ActualDividendTargetGapText = $"NT${actualDividendTargetGapTwd:N0}",
            ActualDividendPieStyle = $"background: conic-gradient(var(--mint) 0deg {actualDividendPieDegrees:0.##}deg, rgba(255, 255, 255, 0.08) {actualDividendPieDegrees:0.##}deg 360deg);",
            ActualDividendNotes = "2330 先以 TSM 的目前配息率模擬；CSNDX、CSPX、VWRA 則沿用 EXXT、IUSA、VWRL 的模擬現金股利設定。",
            WeeklyBuyTuesdayCount = weeklyBuyTuesdayCount,
            WeeklyBuyPlanWindowText = weeklyBuyPlanWindowText,
            WeeklyBuyTotalTwd = weeklyBuyTotalTwd,
            WeeklyBuyTotalText = $"NT${weeklyBuyTotalTwd:N0}",
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
            ActualDividendBreakdowns = actualDividendBreakdowns,
            WeeklyBuyPlans = weeklyBuyPlans.Select(plan => new WeeklyBuyPlanViewModel
            {
                Ticker = plan.Ticker,
                CurrentHoldingTicker = plan.CurrentHoldingTicker,
                TargetValueText = $"NT${plan.TargetValueTwd:N0}",
                CurrentValueText = $"NT${plan.CurrentValueTwd:N0}",
                GapValueText = $"NT${plan.GapValueTwd:N0}",
                WeeklyBuyText = $"NT${plan.WeeklyBuyTwd:N0}",
                WeeklyBuyQuantityText = plan.WeeklyBuyQuantityText,
                NoteText = plan.NoteText
            }).ToList(),
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

    private static List<ActualHoldingDisplay> BuildActualHoldingDisplays(RetirementPlanSnapshot staticSnapshot, RetirementPlanSnapshot? liveSnapshot, IReadOnlyList<InvestmentPosition> dividendSourcePositions, decimal usdToTwdRate)
    {
        var liveLookup = (liveSnapshot?.Positions ?? [])
            .GroupBy(position => position.Ticker, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var dividendSourceLookup = dividendSourcePositions
            .GroupBy(position => position.Ticker, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var displays = new List<ActualHoldingDisplay>();

        foreach (var holding in staticSnapshot.ActualHoldings)
        {
            var dividendSourceTicker = ResolveDividendSourceTicker(holding.Ticker);
            dividendSourceLookup.TryGetValue(dividendSourceTicker, out var dividendSourcePosition);

            if (liveLookup.TryGetValue(holding.Ticker, out var livePosition))
            {
                var marketValueTwd = ConvertToTwd(livePosition.Quantity * livePosition.PricePerShare, livePosition.Currency, usdToTwdRate);
                displays.Add(new ActualHoldingDisplay
                {
                    Ticker = holding.Ticker,
                    Name = holding.Name,
                    MarketValueTwd = marketValueTwd,
                    AnnualDividendTwd = CalculateActualHoldingAnnualDividendTwd(holding.Ticker, marketValueTwd, livePosition.Quantity, dividendSourcePosition, usdToTwdRate),
                    DividendNote = BuildActualDividendNote(holding.Ticker, dividendSourcePosition),
                    DetailText = BuildActualHoldingDetailText(livePosition.Quantity, livePosition.PricePerShare, livePosition.Currency, holding.Note, marketValueTwd)
                });

                continue;
            }

            displays.Add(new ActualHoldingDisplay
            {
                Ticker = holding.Ticker,
                Name = holding.Name,
                MarketValueTwd = holding.MarketValueTwd,
                AnnualDividendTwd = CalculateActualHoldingAnnualDividendTwd(holding.Ticker, holding.MarketValueTwd, holding.Quantity, dividendSourcePosition, usdToTwdRate),
                DividendNote = BuildActualDividendNote(holding.Ticker, dividendSourcePosition),
                DetailText = BuildActualHoldingDetailText(holding.Quantity, holding.PricePerShare, holding.Currency, holding.Note, holding.MarketValueTwd)
            });
        }

        return displays;
    }

    private static List<WeeklyBuyPlanDisplay> BuildWeeklyBuyPlans(IReadOnlyList<ActualHoldingDisplay> actualHoldings, IReadOnlyList<InvestmentPosition> targetPositions, int tuesdayCount, decimal usdToTwdRate)
    {
        var actualLookup = actualHoldings.ToDictionary(holding => holding.Ticker, StringComparer.OrdinalIgnoreCase);
        var plans = new List<WeeklyBuyPlanDisplay>();

        foreach (var targetPosition in targetPositions)
        {
            var targetValueTwd = ConvertToTwd(targetPosition.Quantity * targetPosition.PricePerShare, targetPosition.Currency, usdToTwdRate);
            var currentHoldingTicker = ResolveCurrentHoldingTicker(targetPosition.Ticker);
            var currentHoldingValueTwd = actualLookup.TryGetValue(currentHoldingTicker, out var actualHolding)
                ? actualHolding.MarketValueTwd
                : 0m;
            var gapValueTwd = Math.Max(0m, targetValueTwd - currentHoldingValueTwd);
            var weeklyBuyTwd = tuesdayCount <= 0 ? gapValueTwd : gapValueTwd / tuesdayCount;
            var weeklyBuyQuantityText = BuildWeeklyBuyQuantityText(targetPosition, weeklyBuyTwd, usdToTwdRate, currentHoldingTicker);
            var noteText = currentHoldingTicker == targetPosition.Ticker
                ? $"以目前 {currentHoldingTicker} 持股為基礎分攤"
                : $"以目前 {currentHoldingTicker} 視為台積電部位換算";

            plans.Add(new WeeklyBuyPlanDisplay
            {
                Ticker = targetPosition.Ticker,
                CurrentHoldingTicker = currentHoldingTicker,
                TargetValueTwd = targetValueTwd,
                CurrentValueTwd = currentHoldingValueTwd,
                GapValueTwd = gapValueTwd,
                WeeklyBuyTwd = weeklyBuyTwd,
                WeeklyBuyQuantityText = weeklyBuyQuantityText,
                NoteText = noteText
            });
        }

        return plans;
    }

    private static int CountRemainingWeekdayOccurrences(DateTime startDate, DateTime endDate, DayOfWeek dayOfWeek)
    {
        if (startDate.Date > endDate.Date)
        {
            return 0;
        }

        var count = 0;
        for (var date = startDate.Date; date <= endDate.Date; date = date.AddDays(1))
        {
            if (date.DayOfWeek == dayOfWeek)
            {
                count++;
            }
        }

        return count;
    }

    private static string ResolveCurrentHoldingTicker(string targetTicker)
    {
        return targetTicker.ToUpperInvariant() switch
        {
            "TSM" => "2330",
            _ => targetTicker
        };
    }

    private static string ResolveDividendSourceTicker(string actualHoldingTicker)
    {
        return actualHoldingTicker.ToUpperInvariant() switch
        {
            "2330" => "TSM",
            _ => actualHoldingTicker
        };
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

    private static decimal CalculateActualHoldingAnnualDividendTwd(string actualHoldingTicker, decimal marketValueTwd, decimal? quantity, InvestmentPosition? dividendSourcePosition, decimal usdToTwdRate)
    {
        if (dividendSourcePosition is null)
        {
            return 0m;
        }

        if (!actualHoldingTicker.Equals("2330", StringComparison.OrdinalIgnoreCase) && quantity is > 0)
        {
            return ConvertToTwd(quantity.Value * dividendSourcePosition.AnnualDividendPerShare, dividendSourcePosition.Currency, usdToTwdRate);
        }

        var pricePerShareTwd = ConvertToTwd(dividendSourcePosition.PricePerShare, dividendSourcePosition.Currency, usdToTwdRate);
        if (pricePerShareTwd <= 0)
        {
            return 0m;
        }

        var annualYieldRate = ConvertToTwd(dividendSourcePosition.AnnualDividendPerShare, dividendSourcePosition.Currency, usdToTwdRate) / pricePerShareTwd;
        return marketValueTwd * annualYieldRate;
    }

    private static string BuildActualDividendNote(string actualHoldingTicker, InvestmentPosition? dividendSourcePosition)
    {
        if (dividendSourcePosition is null)
        {
            return string.Empty;
        }

        if (actualHoldingTicker.Equals("2330", StringComparison.OrdinalIgnoreCase))
        {
            return "以 TSM 配息率模擬";
        }

        return string.IsNullOrWhiteSpace(dividendSourcePosition.AnnualDividendProxyTicker)
            ? "依目前設定估算"
            : $"以 {dividendSourcePosition.AnnualDividendProxyTicker} 配息率模擬";
    }

    private static string BuildWeeklyBuyQuantityText(InvestmentPosition targetPosition, decimal weeklyBuyTwd, decimal usdToTwdRate, string currentHoldingTicker)
    {
        if (!currentHoldingTicker.Equals(targetPosition.Ticker, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        var pricePerShareTwd = ConvertToTwd(targetPosition.PricePerShare, targetPosition.Currency, usdToTwdRate);
        if (pricePerShareTwd <= 0)
        {
            return string.Empty;
        }

        var quantity = weeklyBuyTwd / pricePerShareTwd;
        return $"依目前價格，約每週買入 {quantity:N2} 股";
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

        public decimal AnnualDividendTwd { get; init; }

        public string DividendNote { get; init; } = string.Empty;

        public string DetailText { get; init; } = string.Empty;
    }

    private sealed class WeeklyBuyPlanDisplay
    {
        public string Ticker { get; init; } = string.Empty;

        public string CurrentHoldingTicker { get; init; } = string.Empty;

        public decimal TargetValueTwd { get; init; }

        public decimal CurrentValueTwd { get; init; }

        public decimal GapValueTwd { get; init; }

        public decimal WeeklyBuyTwd { get; init; }

        public string WeeklyBuyQuantityText { get; init; } = string.Empty;

        public string NoteText { get; init; } = string.Empty;
    }
}
