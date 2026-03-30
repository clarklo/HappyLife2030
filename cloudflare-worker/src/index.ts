export interface Env {
  PLAN_CACHE?: KVNamespace;
  PAGES_BASE_URL?: string;
  CORS_ORIGIN?: string;
  USD_TWD_RATE?: string;
  TWELVE_DATA_API_KEY?: string;
  IB_FLEX_TOKEN?: string;
  IB_FLEX_QUERY_ID?: string;
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

type TwelveDataPrice = {
  price?: string;
  symbol?: string;
  status?: string;
  code?: number | string;
  message?: string;
};

type FlexPosition = {
  ticker: string;
  name: string;
  currency: string;
  quantity: number;
  pricePerShare: number;
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
const CACHE_TTL_SECONDS = 60 * 60 * 24 * 7;
const REFRESH_INTERVAL_MINUTES = 24 * 60;
const TWELVE_DATA_PRICE_URL = "https://api.twelvedata.com/price";
const FLEX_SEND_REQUEST_URL = "https://ndcdyn.interactivebrokers.com/AccountManagement/FlexWebService/SendRequest";
const FLEX_GET_STATEMENT_URL = "https://gdcdyn.interactivebrokers.com/AccountManagement/FlexWebService/GetStatement";
const FLEX_POLL_DELAY_MS = 5000;
const FLEX_MAX_POLL_ATTEMPTS = 12;
const PLAN_TICKERS = new Set(["TSM", "CSNDX", "CSPX", "VWRA"]);
const PENDING_FLEX_ERROR_CODES = new Set(["1001", "1003", "1004", "1005", "1006", "1007", "1008", "1009", "1019", "1021"]);

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
          quoteProvider: "twelve-data-tsm",
          flexConfigured: Boolean(env.IB_FLEX_TOKEN && env.IB_FLEX_QUERY_ID),
          apiKeyConfigured: Boolean(env.TWELVE_DATA_API_KEY),
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
  const basePlan = await loadCurrentSnapshot(env);
  const flexToken = env.IB_FLEX_TOKEN?.trim();
  const flexQueryId = env.IB_FLEX_QUERY_ID?.trim();
  const apiKey = env.TWELVE_DATA_API_KEY?.trim();
  const refreshErrors: string[] = [];
  const refreshNotes: string[] = [];
  const sourceParts: string[] = [];
  let tsmPrice: number | null = null;
  let flexPositions: FlexPosition[] = [];

  if (flexToken && flexQueryId) {
    try {
      flexPositions = await fetchIbFlexPositions(flexToken, flexQueryId);
      sourceParts.push("IBKR Flex Web Service");
      refreshNotes.push(`Updated ${flexPositions.length} symbol(s) from Flex Query ${flexQueryId}.`);
    } catch (error) {
      refreshErrors.push(`IB Flex: ${toErrorMessage(error)}`);
    }
  } else {
    refreshNotes.push("IB_FLEX_TOKEN or IB_FLEX_QUERY_ID is missing, so the Worker keeps the last cached holdings snapshot.");
  }

  if (apiKey) {
    try {
      tsmPrice = await fetchTsmPrice(apiKey);
      sourceParts.push("Twelve Data (TSM)");
      refreshNotes.push("Updated TSM price from Twelve Data.");
    } catch (error) {
      refreshErrors.push(`TSM: ${toErrorMessage(error)}`);
    }
  } else {
    refreshNotes.push("TWELVE_DATA_API_KEY is missing, so TSM keeps the last cached price.");
  }

  const refreshedAt = new Date().toISOString();
  const flexUpdates = new Map(flexPositions.map((position) => [position.ticker, position]));

  const positions = basePlan.positions.map((position) => {
    const flexUpdate = flexUpdates.get(position.ticker);
    return {
      ...position,
      name: flexUpdate?.name ?? position.name,
      currency: flexUpdate?.currency ?? position.currency,
      quantity: flexUpdate?.quantity ?? position.quantity,
      pricePerShare: position.ticker === "TSM" && tsmPrice !== null
        ? toMoney(tsmPrice)
        : flexUpdate?.pricePerShare !== undefined
          ? toMoney(flexUpdate.pricePerShare)
          : position.pricePerShare
    };
  });

  if (refreshErrors.length > 0) {
    refreshNotes.push(`Partial refresh failures: ${refreshErrors.join("; ")}.`);
  }

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
      source: sourceParts.length > 0 ? sourceParts.join(" + ") : basePlan.liveData.source,
      lastUpdatedUtc: refreshedAt,
      refreshIntervalMinutes: REFRESH_INTERVAL_MINUTES,
      notes: refreshNotes.join(" ")
    },
    history: [
      {
        date: refreshedAt,
        assetValueTwd: toMoney(assetValueTwd),
        note: sourceParts.length > 0
          ? `${sourceParts.join(" + ")} updated the dashboard snapshot.`
          : "Worker kept the latest cached dashboard snapshot unchanged."
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

async function fetchTsmPrice(apiKey: string): Promise<number> {
  const endpoint = `${TWELVE_DATA_PRICE_URL}?symbol=TSM&apikey=${encodeURIComponent(apiKey)}`;
  const response = await fetchJson<TwelveDataPrice>(endpoint);

  if (response.status === "error" || response.code || response.message) {
    throw new Error(response.message ?? `Twelve Data error ${response.code ?? "unknown"}`);
  }

  const numericPrice = Number(response.price);
  if (!Number.isFinite(numericPrice) || numericPrice <= 0) {
    throw new Error("Twelve Data did not return a valid TSM price.");
  }

  return numericPrice;
}

async function fetchIbFlexPositions(token: string, queryId: string): Promise<FlexPosition[]> {
  const requestUrl = `${FLEX_SEND_REQUEST_URL}?t=${encodeURIComponent(token)}&q=${encodeURIComponent(queryId)}&v=3`;
  const requestXml = await fetchText(requestUrl);
  ensureFlexSuccess(requestXml, "SendRequest");

  const referenceCode = readXmlText(requestXml, "ReferenceCode");
  if (!referenceCode) {
    throw new Error("Flex SendRequest succeeded but did not return a reference code.");
  }

  for (let attempt = 1; attempt <= FLEX_MAX_POLL_ATTEMPTS; attempt += 1) {
    const statementUrl = `${FLEX_GET_STATEMENT_URL}?t=${encodeURIComponent(token)}&q=${encodeURIComponent(referenceCode)}&v=3`;
    const statementXml = await fetchText(statementUrl);

    if (statementXml.includes("<FlexQueryResponse") || statementXml.includes("<FlexStatements")) {
      const positions = parseFlexPositions(statementXml);
      if (positions.length === 0) {
        throw new Error("Flex statement did not contain any matching symbols.");
      }

      return positions;
    }

    const status = readXmlText(statementXml, "Status");
    const errorCode = readXmlText(statementXml, "ErrorCode");
    const errorMessage = readXmlText(statementXml, "ErrorMessage");

    if (status === "Fail" && errorCode && PENDING_FLEX_ERROR_CODES.has(errorCode) && attempt < FLEX_MAX_POLL_ATTEMPTS) {
      await sleep(FLEX_POLL_DELAY_MS);
      continue;
    }

    ensureFlexSuccess(statementXml, "GetStatement");
    throw new Error(`Flex statement did not contain a report payload. Error ${errorCode}: ${errorMessage}`);
  }

  throw new Error("Flex statement polling timed out before the report became available.");
}

function parseFlexPositions(xml: string): FlexPosition[] {
  const positions: FlexPosition[] = [];
  const matches = xml.matchAll(/<OpenPosition\b([^>]*)\/>/g);

  for (const match of matches) {
    const attributes = parseXmlAttributes(match[1]);
    const ticker = normalizeSymbol(attributes.symbol ?? attributes.underlyingSymbol);
    if (!ticker || !PLAN_TICKERS.has(ticker)) {
      continue;
    }

    const price = toPositiveNumber(attributes.markPrice ?? attributes.price ?? attributes.closePrice ?? attributes.mtmPrice);
    if (price === null) {
      continue;
    }

    const quantity = toPositiveNumber(attributes.position ?? attributes.quantity) ?? 0;

    positions.push({
      ticker,
      name: decodeXml(attributes.description ?? ticker),
      currency: attributes.currency ?? "USD",
      quantity,
      pricePerShare: price
    });
  }

  return positions;
}

function parseXmlAttributes(input: string): Record<string, string> {
  const attributes: Record<string, string> = {};
  const matches = input.matchAll(/([A-Za-z0-9]+)="([^"]*)"/g);

  for (const match of matches) {
    attributes[match[1]] = decodeXml(match[2]);
  }

  return attributes;
}

function readXmlText(xml: string, tagName: string): string | null {
  const match = xml.match(new RegExp(`<${tagName}>([^<]*)</${tagName}>`));
  return match ? decodeXml(match[1]) : null;
}

function ensureFlexSuccess(xml: string, operationName: string): void {
  const status = readXmlText(xml, "Status");
  if (status === "Success") {
    return;
  }

  const errorCode = readXmlText(xml, "ErrorCode");
  const errorMessage = readXmlText(xml, "ErrorMessage");
  throw new Error(`${operationName} failed. Error ${errorCode}: ${errorMessage}`);
}

function normalizeSymbol(value?: string): string | null {
  if (!value) {
    return null;
  }

  const token = value
    .split(/[ .:/]/)
    .map((item) => item.trim())
    .find(Boolean);

  return token ? token.toUpperCase() : null;
}

function toPositiveNumber(value?: string): number | null {
  if (!value) {
    return null;
  }

  const numericValue = Number(value);
  return Number.isFinite(numericValue) && numericValue > 0 ? numericValue : null;
}

function decodeXml(value: string): string {
  return value
    .replaceAll("&amp;", "&")
    .replaceAll("&quot;", "\"")
    .replaceAll("&apos;", "'")
    .replaceAll("&lt;", "<")
    .replaceAll("&gt;", ">");
}

async function fetchText(url: string, init?: RequestInit): Promise<string> {
  const response = await fetch(url, {
    ...init,
    headers: {
      "Cache-Control": "no-cache",
      ...(init?.headers ?? {})
    }
  });

  if (!response.ok) {
    const body = await response.text();
    throw new Error(`Request failed: ${response.status} ${response.statusText}. ${body}`);
  }

  return response.text();
}

async function sleep(ms: number): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve, ms));
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
