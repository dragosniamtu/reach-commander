import { Page, Route } from "@playwright/test";

export type SourceAccess = "readOnly" | "readWrite";
export type SourceOperationPhase =
  | "accepted"
  | "validating"
  | "applying"
  | "restarting"
  | "healthChecking"
  | "completed"
  | "rolledBack"
  | "failed";

export interface SourceFixture {
  readonly id: string;
  readonly name: string;
  readonly isAvailable: boolean;
  readonly isReadOnly: boolean;
  readonly totalBytes: number | null;
  readonly usedBytes: number | null;
  readonly freeBytes: number | null;
  readonly defaultLeft: boolean;
  readonly defaultRight: boolean;
}

export interface SourceAddRequestFixture {
  readonly displayName: string;
  readonly hostPath: string;
  readonly access: SourceAccess;
}

export interface SourceOperationFixture {
  readonly operationId: string;
  readonly sourceId: string | null;
  readonly displayName: string;
  readonly phase: SourceOperationPhase;
  readonly reasonCode: string;
  readonly detail: string;
  readonly createdAt: string;
  readonly updatedAt: string;
}

export interface SourceOperationUpdate {
  readonly sourceId?: string | null;
  readonly displayName?: string;
  readonly phase?: SourceOperationPhase;
  readonly failedReason?:
    | "validation_failed"
    | "untrusted_source_ancestry"
    | "source_management_failed";
  readonly createdAt?: string;
  readonly updatedAt?: string;
}

export interface InstallerManagedSourceFixture {
  readonly addRequests: readonly SourceAddRequestFixture[];
  readonly operationReads: number;
  publish(operation: SourceOperationUpdate): void;
  disconnectNextOperationRead(): void;
}

const operationId = "77777777-7777-4777-8777-777777777777";
const timestamp = "2026-08-31T12:00:00Z";

export const baseSources: readonly SourceFixture[] = [
  source("downloads", "Downloads", false, true, false),
  source("media", "Media", false, false, true),
  source("usb", "USB", true, false, false),
  source("archive", "Archive", true, false, false),
];

function sourceOperation(overrides: SourceOperationUpdate = {}): SourceOperationFixture {
  const { failedReason, ...operationOverrides } = overrides;
  const phase = operationOverrides.phase ?? "accepted";
  const status = canonicalOperationStatus(phase, failedReason);
  return {
    operationId,
    sourceId: null,
    displayName: "Family media",
    phase,
    reasonCode: status.reasonCode,
    detail: status.detail,
    createdAt: timestamp,
    updatedAt: timestamp,
    ...operationOverrides,
  };
}

export async function routeUnsupportedSourceManagement(
  page: Page,
): Promise<void> {
  await page.route("**/api/source-management/status", (route) => route.fulfill({
    status: 200,
    contentType: "application/json",
    json: {
      supported: false,
      reasonCode: "unsupported_deployment",
      detail: "Source management is unavailable on this installation.",
    },
  }));
}

export async function routeInstallerManagedSourceManagement(
  page: Page,
): Promise<InstallerManagedSourceFixture> {
  let current = sourceOperation();
  let disconnectNext = false;
  let operationReads = 0;
  const addRequests: SourceAddRequestFixture[] = [];

  await page.route("**/api/source-management/**", async (route: Route) => {
    const request = route.request();
    const pathname = new URL(request.url()).pathname;

    if (pathname === "/api/source-management/status") {
      await route.fulfill({
        status: 200,
        contentType: "application/json",
        json: {
          supported: true,
          reasonCode: "supported",
          detail: "Source management is available.",
        },
      });
      return;
    }

    if (pathname === "/api/source-management/sources" && request.method() === "POST") {
      addRequests.push(request.postDataJSON() as SourceAddRequestFixture);
      const accepted = current;
      current = accepted;
      await route.fulfill({
        status: 202,
        contentType: "application/json",
        headers: { Location: `/api/source-management/operations/${accepted.operationId}` },
        json: accepted,
      });
      return;
    }

    if (pathname === `/api/source-management/operations/${operationId}`) {
      operationReads += 1;
      if (disconnectNext) {
        disconnectNext = false;
        await route.abort("connectionreset");
        return;
      }
      await route.fulfill({ status: 200, contentType: "application/json", json: current });
      return;
    }

    await route.fulfill({
      status: 404,
      contentType: "application/problem+json",
      json: { code: "source_operation_not_found", detail: "The operation was not found." },
    });
  });

  await page.route("**/api/sources", async (route: Route) => {
    const generated = current.phase === "completed" && current.sourceId
      ? [
          ...baseSources,
          source(
            current.sourceId,
            current.displayName,
            addRequests.at(-1)?.access === "readOnly",
            false,
            false,
          ),
        ]
      : baseSources;
    await route.fulfill({ status: 200, contentType: "application/json", json: generated });
  });

  return {
    addRequests,
    get operationReads() { return operationReads; },
    publish(operation) {
      current = sourceOperation(operation);
    },
    disconnectNextOperationRead() {
      disconnectNext = true;
    },
  };
}

function canonicalOperationStatus(
  phase: SourceOperationPhase,
  failedReason: SourceOperationUpdate["failedReason"],
): Pick<SourceOperationFixture, "reasonCode" | "detail"> {
  switch (phase) {
    case "accepted":
      return { reasonCode: "accepted", detail: "Source change accepted." };
    case "validating":
      return { reasonCode: "in_progress", detail: "The source change is being validated." };
    case "applying":
      return { reasonCode: "in_progress", detail: "The source configuration is being applied." };
    case "restarting":
      return { reasonCode: "in_progress", detail: "ReachCommander is restarting." };
    case "healthChecking":
      return { reasonCode: "in_progress", detail: "ReachCommander is being checked." };
    case "completed":
      return { reasonCode: "completed", detail: "The source has been added." };
    case "rolledBack":
      return { reasonCode: "rolled_back", detail: "The source change was rolled back." };
    case "failed": {
      const reasonCode = failedReason ?? "source_management_failed";
      return {
        reasonCode,
        detail: reasonCode === "untrusted_source_ancestry"
          ? "The source folder's parent directories must be root-owned and not group- or world-writable."
          : "The source-management operation could not be completed.",
      };
    }
  }
}

function source(
  id: string,
  name: string,
  readOnly: boolean,
  defaultLeft: boolean,
  defaultRight: boolean,
): SourceFixture {
  return {
    id,
    name,
    isAvailable: true,
    isReadOnly: readOnly,
    totalBytes: 1_000_000,
    usedBytes: 250_000,
    freeBytes: 750_000,
    defaultLeft,
    defaultRight,
  };
}
