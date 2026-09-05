import { afterEach, describe, expect, it, vi } from "vitest";
import {
  adminFetch, adminUsers, adminDeleteAsset, putAsset,
  adminConflicts, adminResolveConflict, analyticsSummary, analyticsFunnel, analyticsHealth,
  readSource,
} from "../src/lib/api.js";

// fetch is mocked per test — these are contract tests for the thin client:
// auth header, typed 401, JSON/text switching, and path encoding.

function mockFetch(impl) {
  const fn = vi.fn(impl);
  globalThis.fetch = fn;
  return fn;
}

const jsonResponse = (obj, status = 200) =>
  new Response(JSON.stringify(obj), {
    status,
    headers: { "content-type": "application/json" },
  });

afterEach(() => {
  delete globalThis.fetch;
  vi.restoreAllMocks();
});

describe("adminFetch", () => {
  it("attaches the bearer token and parses JSON", async () => {
    const fn = mockFetch(async () => jsonResponse({ ok: true }));
    const out = await adminFetch("/v1/admin/users", "tok123");
    expect(out).toEqual({ ok: true });
    const [, opt] = fn.mock.calls[0];
    expect(opt.headers.Authorization).toBe("Bearer tok123");
  });

  it("throws the typed '401' error so the UI can prompt for a token", async () => {
    mockFetch(async () => new Response("unauthorized", { status: 401 }));
    await expect(adminFetch("/v1/admin/users", "bad")).rejects.toThrow(/^401$/);
  });

  it("surfaces the server's error text on non-auth failures", async () => {
    mockFetch(async () => new Response("grant failed: disk full\n", { status: 500 }));
    await expect(adminFetch("/v1/admin/grant", "tok")).rejects.toThrow("grant failed: disk full");
  });

  it("returns text for non-JSON responses", async () => {
    mockFetch(async () => new Response("plain body", { status: 200 }));
    await expect(adminFetch("/x", "tok")).resolves.toBe("plain body");
  });
});

describe("wrappers", () => {
  it("adminUsers unwraps to an array even when the field is missing", async () => {
    mockFetch(async () => jsonResponse({}));
    await expect(adminUsers("tok")).resolves.toEqual([]);
  });

  it("adminDeleteAsset percent-encodes segments but keeps '/' separators", async () => {
    const fn = mockFetch(async () => jsonResponse({ deleted: true }));
    await adminDeleteAsset("bg/night #2.png", "tok");
    const [url, opt] = fn.mock.calls[0];
    expect(url).toBe("/v1/admin/assets/bg/night%20%232.png");
    expect(opt.method).toBe("DELETE");
  });

  it("putAsset strips a leading /content/ prefix before uploading", async () => {
    const fn = mockFetch(async () => jsonResponse({ path: "scripts/ch1.lvn", bytes: 2 }));
    await putAsset("/content/scripts/ch1.lvn", "hi", "tok", "text/plain");
    const [url] = fn.mock.calls[0];
    expect(url).toBe("/v1/admin/assets/scripts/ch1.lvn");
  });

  // Версия, на которой правил автор, уезжает на сервер: без неё второй
  // сохранивший молча затирает работу первого, и оба видят «сохранено».
  it("putAsset передаёт версию файла, на которой правил автор", async () => {
    const fn = mockFetch(async () => jsonResponse({ path: "scripts/ch1.lvns", bytes: 2 }));
    await putAsset("scripts/ch1.lvns", "текст", "tok", "text/plain", '"4e-abc"');
    const [, opt] = fn.mock.calls[0];
    expect(opt.headers["If-Match"]).toBe('"4e-abc"');
  });

  it("putAsset без версии не шлёт If-Match — сборке и загрузке арта он не нужен", async () => {
    const fn = mockFetch(async () => jsonResponse({ path: "bg/room.jpg", bytes: 2 }));
    await putAsset("bg/room.jpg", "…", "tok", "image/jpeg");
    const [, opt] = fn.mock.calls[0];
    expect(opt.headers["If-Match"]).toBeUndefined();
  });

  // 409 обязан отличаться от прочих отказов ПРИЗНАКОМ, а не текстом: интерфейс
  // на нём показывает выбор «взять серверную / оставить мою», а разбирать для
  // этого сообщение сервера значило бы сломаться на первой же его правке.
  it("putAsset помечает конфликт версий отдельным признаком", async () => {
    mockFetch(async () => jsonResponse({ conflict: true, error: "файл изменился на сервере" }, 409));
    await expect(putAsset("scripts/ch1.lvns", "текст", "tok", "text/plain", '"старая"'))
      .rejects.toMatchObject({ conflict: true });
  });
});

describe("readSource", () => {
  it("отдаёт текст и версию — то, чем сверяются перед записью", async () => {
    mockFetch(async () => new Response("сцена ch1", { status: 200, headers: { ETag: '"4e-abc"' } }));
    await expect(readSource("scripts/ch1.lvns")).resolves.toEqual({ text: "сцена ch1", etag: '"4e-abc"' });
  });

  // Новая глава — это отсутствующий файл, а не ошибка: сохранение её создаст.
  it("на отсутствующий файл отвечает пустотой, а не исключением", async () => {
    mockFetch(async () => new Response("not found", { status: 404 }));
    await expect(readSource("scripts/новая.lvns")).resolves.toEqual({ text: null, etag: null });
  });
});

describe("import conflicts", () => {
  it("asks for the plain listing by default", async () => {
    const fn = mockFetch(async () => jsonResponse({ count: 0, conflicts: [] }));
    await adminConflicts("tok");
    expect(fn.mock.calls[0][0]).toBe("/v1/admin/import-conflicts");
  });

  it("polls without diffing when only the count is wanted", async () => {
    const fn = mockFetch(async () => jsonResponse({ count: 2 }));
    await adminConflicts("tok", { diff: false });
    expect(fn.mock.calls[0][0]).toBe("/v1/admin/import-conflicts?diff=0");
  });

  it("narrows to one path (which also raises the server's diff budget)", async () => {
    const fn = mockFetch(async () => jsonResponse({ count: 1, conflicts: [] }));
    await adminConflicts("tok", { rel: "scripts/n/ch 1.lvn" });
    expect(fn.mock.calls[0][0]).toBe("/v1/admin/import-conflicts?rel=scripts%2Fn%2Fch%201.lvn");
  });

  it("posts the chosen side as JSON", async () => {
    const fn = mockFetch(async () => jsonResponse({ resolved: true, choice: "incoming" }));
    await adminResolveConflict("scripts/n/ch1.lvn", "incoming", "tok");
    const [url, opt] = fn.mock.calls[0];
    expect(url).toBe("/v1/admin/import-conflicts/resolve");
    expect(opt.method).toBe("POST");
    expect(JSON.parse(opt.body)).toEqual({ rel: "scripts/n/ch1.lvn", choice: "incoming", title: "" });
  });

  it("surfaces a 422 rejection as the validator's own error lines", async () => {
    // The resolve endpoint answers an invalid .lvn with {errors:[…]}; the UI
    // must show WHY the version cannot ship, not a raw JSON blob.
    mockFetch(async () => new Response(
      JSON.stringify({ rejected: true, errors: ["not a .lvn document: invalid character 'l'"] }),
      { status: 422, headers: { "content-type": "application/json" } }));
    await expect(adminResolveConflict("scripts/n/ch1.lvn", "incoming", "tok"))
      .rejects.toThrow(/not a \.lvn document/);
  });
});

describe("analytics reports", () => {
  it("passes the window through and omits '?' when it is empty", async () => {
    const fn = mockFetch(async () => jsonResponse({ total: 0 }));
    await analyticsSummary("days=7", "tok");
    await analyticsSummary("", "tok");
    expect(fn.mock.calls[0][0]).toBe("/v1/analytics/summary?days=7");
    expect(fn.mock.calls[1][0]).toBe("/v1/analytics/summary");
  });

  it("joins the window and the title on the funnel endpoint", async () => {
    const fn = mockFetch(async () => jsonResponse({ funnel: {} }));
    await analyticsFunnel("days=7&min=5", "novel", "tok");
    await analyticsFunnel("", "", "tok");
    expect(fn.mock.calls[0][0]).toBe("/v1/analytics/funnel?days=7&min=5&title=novel");
    expect(fn.mock.calls[1][0]).toBe("/v1/analytics/funnel?title=");
  });

  it("sends the window to health too", async () => {
    const fn = mockFetch(async () => jsonResponse({ gaps: [] }));
    await analyticsHealth("day=2026-07-26", "tok");
    expect(fn.mock.calls[0][0]).toBe("/v1/analytics/health?day=2026-07-26");
  });
});
