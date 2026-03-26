export interface Env {
  PLAN_CACHE?: KVNamespace;
  PAGES_BASE_URL?: string;
  CORS_ORIGIN?: string;
  USD_TWD_RATE?: string;
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

type Quote = {
  symbol: string;
  regularMarketPrice?: number;
  regularMarketPreviousClose?: number;
  postMarketPrice?: number;
  bid?: number;
  ask?: number;
  trailingAnnualDividendRate?: number;
  dividendRate?: number;
};

type YahooQuoteResponse = {
  quoteResponse?: {
    result?: Quote[];
  };
};

const CACHE_KEY = "retirement-plan-live-v1";
const CACHE_TTL_SECONDS = 300;
const YAHOO_QUOTE_URL = "https://query1.finance.yahoo.com/v7/finance/quote";
const SYMBOLS: Record<string, string> = {
  TSM: "TSM",
  CSNDX: "CSNDX.SW",
  CSPX: "CSPX.L",
  VWRA: "VWRA.L"
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
      return json({ ok: true, cacheEnabled: Boolean(env.PLAN_CACHE) }, env);
    }

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
  },

  async scheduled(_event, env, ctx): Promise<void> {
    ctx.waitUntil(refreshPlan(env, true));
  }
} satisfies ExportedHandler<Env>;

async function refreshPlan(env: Env, persistCache: boolean): Promise<PlanSnapshot> {
  const baseUrl = (env.PAGES_BASE_URL ?? "https://happylife2030.pages.dev").replace(/\/$/, "");
  const basePlan = await fetchJson<PlanSnapshot>(`${baseUrl}/data/retirement-plan.json?ts=${Date.now()}`);
  const usdToTwdRate = Number.parseFloat(env.USD_TWD_RATE ?? `${basePlan.assumptions.usdToTwdRate || 32}`) || 32;
  const quotes = await fetchYahooQuotes(Object.values(SYMBOLS));
  const refreshedAt = new Date().toISOString();

  const positions = basePlan.positions.map((position) => {
    const symbol = SYMBOLS[position.ticker];
    const quote = quotes.get(symbol);
    const marketPrice = quote?.regularMarketPrice ?? quote?.postMarketPrice ?? quote?.regularMarketPreviousClose ?? quote?.bid ?? quote?.ask;
    const annualDividendPerShare = position.ticker === "TSM"
      ? quote?.dividendRate ?? quote?.trailingAnnualDividendRate ?? position.annualDividendPerShare
      : 0;

    return {
      ...position,
      pricePerShare: toMoney(marketPrice ?? position.pricePerShare),
      annualDividendPerShare: toMoney(annualDividendPerShare)
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
      source: "Cloudflare Worker + Yahoo Finance quotes",
      lastUpdatedUtc: refreshedAt,
      refreshIntervalMinutes: 5,
      notes: "股價每 5 分鐘同步一次。若 Worker 暫時失敗，前端會自動回退到 Pages 上的靜態配置資料。"
    },
    history: [
      {
        date: refreshedAt,
        assetValueTwd: toMoney(assetValueTwd),
        note: "Cloudflare Worker 已更新最新價格與可直接領現金的股利估算。"
      },
      ...basePlan.history.filter((point) => point.date !== refreshedAt).slice(0, 11)
    ]
  };

  if (persistCache && env.PLAN_CACHE) {
    await env.PLAN_CACHE.put(CACHE_KEY, JSON.stringify(snapshot), {
      expirationTtl: CACHE_TTL_SECONDS
    });
  }

  return snapshot;
}

async function fetchYahooQuotes(symbols: string[]): Promise<Map<string, Quote>> {
  const endpoint = `${YAHOO_QUOTE_URL}?symbols=${encodeURIComponent(symbols.join(","))}`;
  const payload = await fetchJson<YahooQuoteResponse>(endpoint, {
    headers: {
      "User-Agent": "Mozilla/5.0"
    }
  });

  const map = new Map<string, Quote>();
  for (const quote of payload.quoteResponse?.result ?? []) {
    map.set(quote.symbol, quote);
  }

  return map;
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

function toMoney(value: number): number {
  return Math.round((value + Number.EPSILON) * 100) / 100;
}
