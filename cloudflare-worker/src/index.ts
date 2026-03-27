export interface Env {
  PLAN_CACHE?: KVNamespace;
  PAGES_BASE_URL?: string;
  CORS_ORIGIN?: string;
  USD_TWD_RATE?: string;
  TWELVE_DATA_API_KEY?: string;
}

type PlanSnapshot = {
  asOfDate: string;
  goal: {
    targetDate: string;
    targetMonthlyIncomeTwd: number;
  };
  annualPlan: {
    year: number;
    targetInvestmentTwd: number;
    deadline: string;
  };
  fixedDeposit: {
    principalTwd: number;
    annualInterestRate: number;
    reinvestInterest: boolean;
  };
  liveData: {
    isLive: boolean;
    source: string;
    lastUpdatedUtc: string | null;
    refreshIntervalMinutes: number;
    notes: string;
  };
  assumptions: {
    assumedAnnualYieldRate: number;
    expectedAnnualReturnRate: number;
    usdToTwdRate: number;
    monthlyContributionUsd: number;
    reinvestDividends: boolean;
  };
  currentCashTwd: number;
  notes: string;
  positions: Array<{
    ticker: string;
    name: string;
    currency: string;
    quantity: number;
    pricePerShare: number;
    annualDividendPerShare: number;
    monthlyContributionUsd: number;
  }>;
  history: Array<{
    date: string;
    assetValueTwd: number;
    note: string;
  }>;
};

type TwelveDataQuote = {
  symbol?: string;
  close?: string;
  previous_close?: string;
  currency?: string;
  is_market_open?: boolean;
  code?: number;
  status?: string;
  message?: string;
};

type CurrencyConversionResponse = {
  rate?: string | number;
  code?: number;
  status?: string;
  message?: string;
};

type InstrumentConfig = {
  symbol: string;
  exchange: string;
  marketTimeZone: string;
  marketOpenHour: number;
  marketOpenMinute: number;
  marketCloseHour: number;
  marketCloseMinute: number;
};

const CACHE_KEY = "retirement-plan-live-v2";
const CACHE_TTL_SECONDS = 300;
const TWELVE_DATA_QUOTE_URL = "https://api.twelvedata.com/quote";
const TWELVE_DATA_CURRENCY_CONVERSION_URL = "https://api.twelvedata.com/currency_conversion";

const INSTRUMENTS: Record<string, InstrumentConfig> = {
  TSM: {
    symbol: "TSM",
    exchange: "NYSE",
    marketTimeZone: "America/New_York",
    marketOpenHour: 9,
    marketOpenMinute: 30,
    marketCloseHour: 16,
    marketCloseMinute: 0
  },
  CSNDX: {
    symbol: "CSNDX",
    exchange: "MTA",
    marketTimeZone: "Europe/Rome",
    marketOpenHour: 9,
    marketOpenMinute: 0,
    marketCloseHour: 17,
    marketCloseMinute: 30
  },
  CSPX: {
    symbol: "CSPX",
    exchange: "LSE",
    marketTimeZone: "Europe/London",
    marketOpenHour: 8,
    marketOpenMinute: 0,
    marketCloseHour: 16,
    marketCloseMinute: 30
  },
  VWRA: {
    symbol: "VWRA",
    exchange: "LSE",
    marketTimeZone: "Europe/London",
    marketOpenHour: 8,
    marketOpenMinute: 0,
    marketCloseHour: 16,
    marketCloseMinute: 30
  }
};

export default {
  async fetch(request, env): Promise<Response> {
    if (request.method === "OPTIONS") {
      return new Response(null, {
        headers: buildCorsHeaders(env)
      });
    }

    const url = new URL(request.url);

    if (url.pathname === "/health") {
      return json(
        {
          ok: true,
          cacheEnabled: Boolean(env.PLAN_CACHE),
          quoteProvider: "twelve-data",
          apiKeyConfigured: Boolean(env.TWELVE_DATA_API_KEY)
        },
        env
      );
    }

    try {
      if (url.pathname === "/api/refresh") {
        const snapshot = await refreshPlan(env, true);
        return json(snapshot, env);
      }

      if (url.pathname === "/api/plan") {
        const cached = await env.PLAN_CACHE?.get<PlanSnapshot>(CACHE_KEY, "json");
        if (cached) {
          return json(cached, env);
        }

        const snapshot = await refreshPlan(env, true);
        return json(snapshot, env);
      }

      return json({ error: "Not found" }, env, 404);
    } catch (error) {
      const fallback = await loadBasePlan(env, `Worker fallback: ${toErrorMessage(error)}`);
      return json(fallback, env);
    }
  },

  async scheduled(_event, env, ctx): Promise<void> {
    ctx.waitUntil(refreshPlan(env, false));
  }
} satisfies ExportedHandler<Env>;

async function refreshPlan(env: Env, forceRefreshAllSymbols: boolean): Promise<PlanSnapshot> {
  const basePlan = await loadBasePlan(env);
  const cachedPlan = await env.PLAN_CACHE?.get<PlanSnapshot>(CACHE_KEY, "json");
  const usdToTwdRate = Number.parseFloat(env.USD_TWD_RATE ?? `${basePlan.assumptions.usdToTwdRate || 32}`) || 32;
  const apiKey = env.TWELVE_DATA_API_KEY?.trim();

  let quotes = new Map<string, TwelveDataQuote>();
  let quoteErrors: string[] = [];
  let liveNotes = "股價每 5 分鐘同步一次。若報價來源暫時失敗，前端會自動回退到上一次快取或 Pages 的基準資料。";

  if (apiKey) {
    const tickersToRefresh = forceRefreshAllSymbols
      ? Object.keys(INSTRUMENTS)
      : Object.keys(INSTRUMENTS).filter((ticker) => isMarketOpen(INSTRUMENTS[ticker], new Date()));

    if (tickersToRefresh.length > 0) {
      try {
        const quoteResult = await fetchTwelveDataQuotes(tickersToRefresh, apiKey);
        quotes = quoteResult.quotes;
        quoteErrors = quoteResult.errors;
      } catch (error) {
        liveNotes = `Twelve Data 暫時失敗，這次先沿用上一次快取或 Pages 基準資料。${toErrorMessage(error)}`;
      }
    } else {
      liveNotes = "目前不在交易時段內，這次沿用最近一次的快取價格以節省免費 API 額度。";
    }
  } else {
    liveNotes = "尚未設定 Twelve Data API key，這次先沿用快取或 Pages 基準資料。";
  }

  const refreshedAt = new Date().toISOString();

  if (quoteErrors.length > 0) {
    liveNotes = `${liveNotes} 無法更新的標的：${quoteErrors.join("；")}。`;
  }

  const positions = basePlan.positions.map((position) => {
    const quote = quotes.get(position.ticker);
    const cachedPosition = cachedPlan?.positions.find((cached) => cached.ticker === position.ticker);
    const latestPrice = quote?.close
      ? convertQuotePriceToUsd(quote.close, quote.currency, position.pricePerShare, quotes)
      : position.pricePerShare;

    return {
      ...position,
      pricePerShare: toMoney(latestPrice),
      annualDividendPerShare: toMoney(cachedPosition?.annualDividendPerShare ?? position.annualDividendPerShare)
    };
  });

  const investedTwd = positions.reduce((sum, position) => {
    const fx = position.currency === "USD" ? usdToTwdRate : 1;
    return sum + (position.quantity * position.pricePerShare * fx);
  }, 0);
  const assetValueTwd = investedTwd + basePlan.fixedDeposit.principalTwd + basePlan.currentCashTwd;

  const snapshot: PlanSnapshot = {
    ...basePlan,
    asOfDate: refreshedAt,
    assumptions: {
      ...basePlan.assumptions,
      usdToTwdRate
    },
    positions,
    liveData: {
      isLive: true,
      source: quotes.size > 0 ? "Cloudflare Worker + Twelve Data quotes" : "Cloudflare Worker fallback snapshot",
      lastUpdatedUtc: refreshedAt,
      refreshIntervalMinutes: 5,
      notes: liveNotes
    },
    history: [
      {
        date: refreshedAt,
        assetValueTwd: toMoney(assetValueTwd),
        note: quotes.size > 0 ? "Cloudflare Worker 已用 Twelve Data 更新最新價格。" : "Cloudflare Worker 沿用最近一次可用價格或基準資料。"
      },
      ...basePlan.history.filter((point) => point.date !== refreshedAt).slice(0, 11)
    ]
  };

  if (env.PLAN_CACHE) {
    await env.PLAN_CACHE.put(CACHE_KEY, JSON.stringify(snapshot), {
      expirationTtl: CACHE_TTL_SECONDS
    });
  }

  return snapshot;
}

async function loadBasePlan(env: Env, extraNote?: string): Promise<PlanSnapshot> {
  const baseUrl = (env.PAGES_BASE_URL ?? "https://happylife2030.pages.dev").replace(/\/$/, "");
  const basePlan = await fetchJson<PlanSnapshot>(`${baseUrl}/data/retirement-plan.json?ts=${Date.now()}`);

  if (!extraNote) {
    return basePlan;
  }

  return {
    ...basePlan,
    liveData: {
      isLive: true,
      source: "Cloudflare Worker fallback snapshot",
      lastUpdatedUtc: new Date().toISOString(),
      refreshIntervalMinutes: 5,
      notes: extraNote
    }
  };
}

async function fetchTwelveDataQuotes(
  tickers: string[],
  apiKey: string
): Promise<{ quotes: Map<string, TwelveDataQuote>; errors: string[] }> {
  const quotes = new Map<string, TwelveDataQuote>();
  const errors: string[] = [];

  for (const ticker of tickers) {
    const instrument = INSTRUMENTS[ticker];
    const endpoint = new URL(TWELVE_DATA_QUOTE_URL);
    endpoint.searchParams.set("symbol", instrument.symbol);
    endpoint.searchParams.set("exchange", instrument.exchange);
    endpoint.searchParams.set("apikey", apiKey);

    try {
      const quote = await fetchJson<TwelveDataQuote>(endpoint.toString());
      if (quote.code || quote.status === "error") {
        errors.push(`${ticker}: ${quote.message ?? "Twelve Data quote failed"}`);
        continue;
      }

      quotes.set(ticker, quote);
    } catch (error) {
      errors.push(`${ticker}: ${toErrorMessage(error)}`);
    }
  }

  if (quotes.has("CSNDX")) {
    try {
      const eurUsdRate = await fetchEurUsdRate(apiKey);
      quotes.set("EURUSD", {
        symbol: "EUR/USD",
        close: eurUsdRate.toString(),
        currency: "USD"
      });
    } catch (error) {
      errors.push(`EUR/USD: ${toErrorMessage(error)}`);
    }
  }

  return { quotes, errors };
}

async function fetchEurUsdRate(apiKey: string): Promise<number> {
  const endpoint = new URL(TWELVE_DATA_CURRENCY_CONVERSION_URL);
  endpoint.searchParams.set("symbol", "EUR/USD");
  endpoint.searchParams.set("amount", "1");
  endpoint.searchParams.set("apikey", apiKey);

  const payload = await fetchJson<CurrencyConversionResponse>(endpoint.toString());
  if (payload.code || payload.status === "error") {
    throw new Error(payload.message ?? "EUR/USD conversion failed");
  }

  const rate = Number(payload.rate);
  if (!Number.isFinite(rate) || rate <= 0) {
    throw new Error("無法取得 EUR/USD 匯率");
  }

  return rate;
}

async function fetchJson<T>(url: string, init?: RequestInit): Promise<T> {
  const response = await fetch(url, {
    ...init,
    headers: {
      "Cache-Control": "no-cache",
      ...(init?.headers ?? {})
    }
  });

  if (!response.ok) {
    throw new Error(`Request failed: ${response.status} ${response.statusText}`);
  }

  return response.json<T>();
}

function json(body: unknown, env: Env, status = 200): Response {
  return new Response(JSON.stringify(body, null, 2), {
    status,
    headers: {
      "Content-Type": "application/json; charset=utf-8",
      "Cache-Control": "no-store",
      ...buildCorsHeaders(env)
    }
  });
}

function buildCorsHeaders(env: Env): Record<string, string> {
  return {
    "Access-Control-Allow-Origin": env.CORS_ORIGIN ?? "*",
    "Access-Control-Allow-Methods": "GET,OPTIONS",
    "Access-Control-Allow-Headers": "Content-Type"
  };
}

function convertQuotePriceToUsd(rawPrice: string, currency: string | undefined, fallbackPrice: number, quotes: Map<string, TwelveDataQuote>): number {
  const numericPrice = Number(rawPrice);
  if (!Number.isFinite(numericPrice) || numericPrice <= 0) {
    return fallbackPrice;
  }

  if (!currency || currency === "USD") {
    return numericPrice;
  }

  if (currency === "EUR") {
    const eurUsd = Number(quotes.get("EURUSD")?.close);
    if (Number.isFinite(eurUsd) && eurUsd > 0) {
      return numericPrice * eurUsd;
    }
  }

  return fallbackPrice;
}

function isMarketOpen(instrument: InstrumentConfig, now: Date): boolean {
  const formatter = new Intl.DateTimeFormat("en-GB", {
    timeZone: instrument.marketTimeZone,
    weekday: "short",
    hour: "2-digit",
    minute: "2-digit",
    hour12: false
  });

  const parts = formatter.formatToParts(now);
  const weekday = parts.find((part) => part.type === "weekday")?.value;
  const hour = Number(parts.find((part) => part.type === "hour")?.value ?? "0");
  const minute = Number(parts.find((part) => part.type === "minute")?.value ?? "0");

  if (weekday === "Sat" || weekday === "Sun") {
    return false;
  }

  const currentMinutes = (hour * 60) + minute;
  const openMinutes = (instrument.marketOpenHour * 60) + instrument.marketOpenMinute;
  const closeMinutes = (instrument.marketCloseHour * 60) + instrument.marketCloseMinute;

  return currentMinutes >= openMinutes && currentMinutes <= closeMinutes;
}

function toMoney(value: number): number {
  return Math.round((value + Number.EPSILON) * 100) / 100;
}

function toErrorMessage(error: unknown): string {
  return error instanceof Error ? error.message : "Unknown error";
}
