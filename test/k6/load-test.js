/**
 * k6 — Rinha Fraud API
 *
 * Uso (BASE_URL opcional, default localhost:9999):
 *   k6 run test/k6/load-test.js
 *   k6 run -e BASE_URL=http://127.0.0.1:9999 test/k6/load-test.js
 *
 * Smoke rápido:
 *   k6 run --vus 1 --iterations 5 test/k6/load-test.js
 *
 * Se ~50% das requisições falharem: antes era GET /ready=200 com app sem
 * references.bin e POST=503. Agora /ready só retorna 2xx com dataset carregado.
 * Local: garanta data/references.bin (ou suba docker-compose).
 */

import http from "k6/http";
import { check, sleep } from "k6";

const baseUrl = __ENV.BASE_URL || "http://127.0.0.1:9999";

const reqOpts = {
  tags: { endpoint: "fraud-score" },
};

const readyOpts = {
  tags: { endpoint: "ready" },
};

export const options = {
  thresholds: {
    http_req_failed: ["rate<0.01"],
    http_req_duration: ["p(99)<2000"],
  },
  stages: [
    { duration: "10s", target: 20 },
    { duration: "30s", target: 50 },
    { duration: "10s", target: 0 },
  ],
};

const fraudPayload = JSON.stringify({
  id: "tx-k6-test",
  transaction: {
    amount: 384.88,
    installments: 3,
    requested_at: "2026-03-11T20:23:35Z",
  },
  customer: {
    avg_amount: 769.76,
    tx_count_24h: 3,
    known_merchants: ["MERC-009", "MERC-001"],
  },
  merchant: {
    id: "MERC-001",
    mcc: "5912",
    avg_amount: 298.95,
  },
  terminal: {
    is_online: false,
    card_present: true,
    km_from_home: 13.7090520965,
  },
  last_transaction: {
    timestamp: "2026-03-11T14:58:35Z",
    km_from_current: 18.8626479774,
  },
});

const jsonHeaders = { "Content-Type": "application/json" };

export default function () {
  const readyRes = http.get(`${baseUrl}/ready`, readyOpts);
  check(readyRes, {
    "GET /ready status 2xx": (r) => r.status >= 200 && r.status < 300,
  });

  const scoreRes = http.post(`${baseUrl}/fraud-score`, fraudPayload, {
    headers: jsonHeaders,
    ...reqOpts,
  });
  check(scoreRes, {
    "POST /fraud-score 200": (r) => r.status === 200,
    "body tem approved": (r) => {
      try {
        const b = r.json();
        return typeof b.approved === "boolean" && typeof b.fraud_score === "number";
      } catch {
        return false;
      }
    },
  });

  sleep(0.05);
}

export function setup() {
  const res = http.get(`${baseUrl}/ready`, readyOpts);
  if (res.status < 200 || res.status >= 300) {
    const hint =
      res.status === 503
        ? " Sem references.bin a API não está pronta (copie data/ ou use Docker)."
        : "";
    throw new Error(
      `${baseUrl}/ready retornou HTTP ${res.status}.${hint} Corpo: ${String(res.body).slice(0, 200)}`,
    );
  }

  const probe = http.post(`${baseUrl}/fraud-score`, fraudPayload, {
    headers: jsonHeaders,
    ...reqOpts,
  });
  if (probe.status !== 200) {
    throw new Error(
      `POST /fraud-score smoke falhou (HTTP ${probe.status}). Corpo: ${String(probe.body).slice(0, 300)}`,
    );
  }
}
