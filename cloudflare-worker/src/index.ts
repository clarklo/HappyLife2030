export interface Env {
  PLAN_CACHE?: KVNamespace;
  PAGES_BASE_URL?: string;
  CORS_ORIGIN?: string;
  USD_TWD_RATE?: string;
  LEEWAY_API_TOKEN?: string;
  WORKER_INGEST_TOKEN?: string;
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

type LeewayQuote = {
  code?: string;
  timestamp?: number;
  close?: number | string;
  previousClose?: number | string;
  change?: number | string;
  change_p?: number | string;
  message?: string;
  error?: string;
};

type InstrumentConfig = {
  code: string;
};

type IngestPositionUpdate = {
  ticker: string;
  name?: string;
  currency?: string;
  quantity?: number;
  pricePerShare?: number;
  annualDividendPerShare?: number;
};

type IngestRequest = {
  source?: string;
  notes?: string;
  asOfDate?: string;
  refreshIntervalMinutes?: number;
  currentCashTwd?: number;
  positions?: IngestPositionUpdate[];
};

const CACHE_KEY = "retirement-plan-live-v3";
const CACHE_TTL_SECONDS = 60 * 60 * 2;
const REFRESH_INTERVAL_MINUTES = 120;
const LEEWAY_LIVE_BASE_URL = "https://api.leeway.tech/api/v1/public/live";

const INSTRUMENTS: Record<string, InstrumentConfig> = {
  TSM: { code: "TSM.NYSE" },
  CSNDX: { code: "CSNDX.SW" },
  CSPX: { code: "CSPX.LSE" },
  VWRA: { code: "VWRA.LSE" }
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
          quoteProvider: "leeway",
          apiKeyConfigured: Boolean(env.LEEWAY_API_TOKEN),
          ingestConfigured: Boolean(env.WORKER_INGEST_TOKEN)
        },
        env
      );
    }

    try {
      if (url.pathname === "/api/ingest" && request.method === "POST") {
        assertIngestAuthorized(request, env);
        const payload = await request.json<IngestRequest>();
        const snapshot = await ingestSnapshot(payload, env);
        return json(snapshot, env);
      }

      if (url.pathname === "/api/refresh") {
        const snapshot = await refreshPlan(env);
        return json(snapshot, env);
      }

      if (url.pathname === "/api/plan") {
        const cached = await env.PLAN_CACHE?.get<PlanSnapshot>(CACHE_KEY, "json");
        if (cached) {
          return json(cached, env);
        }

        const snapshot = await refreshPlan(env);
        return json(snapshot, env);
      }

      return json({ error: "Not found" }, env, 404);
    } catch (error) {
      if (error instanceof HttpError) {
        return json({ error: error.message }, env, error.status);
      }

      const fallback = await loadBasePlan(env, `Worker fallback: ${toErrorMessage(error)}`);
      return json(fallback, env);
    }
  },

  async scheduled(_event, env, ctx): Promise<void> {
    ctx.waitUntil(refreshPlan(env));
  }
} satisfies ExportedHandler<Env>;

async function refreshPlan(env: Env): Promise<PlanSnapshot> {
  const basePlan = await loadBasePlan(env);
  const apiToken = env.LEEWAY_API_TOKEN?.trim();

  let quotes = new Map<string, LeewayQuote>();
  let quoteErrors: string[] = [];
  let liveNotes = "Prices sync every 2 hours. If quote fetch fails, the dashboard falls back to the Pages baseline snapshot.";

  if (apiToken) {
    const quoteResult = await fetchLeewayQuotes(apiToken);
    quotes = quoteResult.quotes;
    quoteErrors = quoteResult.errors;
  } else {
    liveNotes = "Leeway API token is missing, so the dashboard is currently using the Pages baseline snapshot.";
  }

  if (quoteErrors.length > 0) {
    liveNotes = `${liveNotes} Partial quote failures: ${quoteErrors.join("; ")}.`;
  }

  const refreshedAt = new Date().toISOString();

  const positions = basePlan.positions.map((position) => {
    const quote = quotes.get(position.ticker);
    const latestPrice = quote?.close !== undefined
      ? toNumber(quote.close, position.pricePerShare)
      : position.pricePerShare;

    return {
      ...position,
      pricePerShare: toMoney(latestPrice)
    };
  });

  const investedTwd = positions.reduce((sum, position) => {
    const fx = position.currency === "USD" ? basePlan.assumptions.usdToTwdRate : 1;
    return sum + (position.quantity * position.pricePerShare * fx);
  }, 0);
  const assetValueTwd = investedTwd + basePlan.fixedDeposit.principalTwd + basePlan.currentCashTwd;

  const snapshot: PlanSnapshot = {
    ...basePlan,
    asOfDate: refreshedAt,
    positions,
    liveData: {
      isLive: true,
      source: quotes.size > 0 ? "Cloudflare Worker + Leeway quotes" : "Cloudflare Worker fallback snapshot",
      lastUpdatedUtc: refreshedAt,
      refreshIntervalMinutes: REFRESH_INTERVAL_MINUTES,
      notes: liveNotes
    },
    history: [
      {
        date: refreshedAt,
        assetValueTwd: toMoney(assetValueTwd),
        note: quotes.size > 0
          ? "Cloudflare Worker refreshed the latest asset values with Leeway quotes."
          : "Cloudflare Worker could not refresh quotes and kept the Pages baseline snapshot."
      },
      ...basePlan.history.filter((point) => point.date !== refreshedAt).slice(0, 11)
    ]
  };

  await storeSnapshot(snapshot, env);

  return snapshot;
}

async function ingestSnapshot(payload: IngestRequest, env: Env): Promise<PlanSnapshot> {
  const baseSnapshot = await loadCurrentSnapshot(env);
  const refreshedAt = payload.asOfDate ?? new Date().toISOString();
  const updates = new Map((payload.positions ?? []).map((position) => [position.ticker.toUpperCase(), position]));

  const positions = baseSnapshot.positions.map((position) => {
    const update = updates.get(position.ticker.toUpperCase());
    if (!update) {
      return position;
    }

    return {
      ...position,
      name: update.name ?? position.name,
      currency: update.currency ?? position.currency,
      quantity: update.quantity ?? position.quantity,
      pricePerShare: update.pricePerShare !== undefined ? toMoney(update.pricePerShare) : position.pricePerShare,
      annualDividendPerShare: update.annualDividendPerShare !== undefined
        ? toMoney(update.annualDividendPerShare)
        : position.annualDividendPerShare
    };
  });

  const investedTwd = positions.reduce((sum, position) => {
    const fx = position.currency === "USD" ? baseSnapshot.assumptions.usdToTwdRate : 1;
    return sum + (position.quantity * position.pricePerShare * fx);
  }, 0);
  const currentCashTwd = payload.currentCashTwd ?? baseSnapshot.currentCashTwd;
  const assetValueTwd = investedTwd + baseSnapshot.fixedDeposit.principalTwd + currentCashTwd;
  const source = payload.source?.trim() || "IBKR Client Portal Gateway";
  const notes = payload.notes?.trim()
    || "Snapshot updated by a local .NET sync job using the IBKR Client Portal Gateway.";
  const refreshIntervalMinutes = payload.refreshIntervalMinutes ?? (24 * 60);

  const snapshot: PlanSnapshot = {
    ...baseSnapshot,
    asOfDate: refreshedAt,
    currentCashTwd,
    positions,
    liveData: {
      isLive: true,
      source,
      lastUpdatedUtc: refreshedAt,
      refreshIntervalMinutes,
      notes
    },
    history: [
      {
        date: refreshedAt,
        assetValueTwd: toMoney(assetValueTwd),
        note: `${source} updated the dashboard snapshot from IBKR.`
      },
      ...baseSnapshot.history.filter((point) => point.date !== refreshedAt).slice(0, 11)
    ]
  };

  await storeSnapshot(snapshot, env);
  return snapshot;
}

async function loadCurrentSnapshot(env: Env): Promise<PlanSnapshot> {
  const cached = await env.PLAN_CACHE?.get<PlanSnapshot>(CACHE_KEY, "json");
  return cached ?? loadBasePlan(env);
}

async function storeSnapshot(snapshot: PlanSnapshot, env: Env): Promise<void> {
  if (!env.PLAN_CACHE) {
    return;
  }

  await env.PLAN_CACHE.put(CACHE_KEY, JSON.stringify(snapshot), {
    expirationTtl: CACHE_TTL_SECONDS
  });
}

function assertIngestAuthorized(request: Request, env: Env): void {
  const expectedToken = env.WORKER_INGEST_TOKEN?.trim();
  if (!expectedToken) {
    throw new HttpError(503, "Worker ingest token is not configured.");
  }

  const header = request.headers.get("Authorization") ?? "";
  const token = header.startsWith("Bearer ") ? header.slice("Bearer ".length).trim() : "";

  if (!token || token !== expectedToken) {
    throw new HttpError(401, "Unauthorized ingest request.");
  }
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
      refreshIntervalMinutes: REFRESH_INTERVAL_MINUTES,
      notes: extraNote
    }
  };
}

async function fetchLeewayQuotes(apiToken: string): Promise<{ quotes: Map<string, LeewayQuote>; errors: string[] }> {
  const quotes = new Map<string, LeewayQuote>();
  const errors: string[] = [];

  for (const [ticker, instrument] of Object.entries(INSTRUMENTS)) {
    const endpoint = `${LEEWAY_LIVE_BASE_URL}/${instrument.code}?apitoken=${encodeURIComponent(apiToken)}`;

    try {
      const quote = await fetchJson<LeewayQuote>(endpoint);
      if (quote.error || quote.message) {
        errors.push(`${ticker}: ${quote.error ?? quote.message}`);
        continue;
      }

      if (quote.close === undefined) {
        errors.push(`${ticker}: missing close price`);
        continue;
      }

      quotes.set(ticker, quote);
    } catch (error) {
      errors.push(`${ticker}: ${toErrorMessage(error)}`);
    }
  }

  return { quotes, errors };
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

function toNumber(value: number | string, fallback: number): number {
  const numericValue = Number(value);
  return Number.isFinite(numericValue) && numericValue > 0 ? numericValue : fallback;
}

function toErrorMessage(error: unknown): string {
  return error instanceof Error ? error.message : "Unknown error";
}

class HttpError extends Error {
  constructor(public readonly status: number, message: string) {
    super(message);
    this.name = "HttpError";
  }
}
