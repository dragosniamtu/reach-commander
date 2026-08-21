import { Inject, Injectable, InjectionToken } from '@angular/core';
import {
  FileSortColumn,
  PanelLocation,
  PanelSide,
  PanelState,
  SortDirection,
} from './commander.models';

export interface PersistedDirectoryTab {
  readonly id: string;
  readonly location: PanelLocation;
}

export interface PersistedPanelState {
  readonly activeTabId: string;
  readonly tabs: readonly PersistedDirectoryTab[];
  readonly sortColumn: FileSortColumn;
  readonly sortDirection: SortDirection;
  readonly filter: string;
}

export interface PersistedCommanderState {
  readonly version: 2;
  readonly activePanel: PanelSide;
  readonly left: PersistedPanelState;
  readonly right: PersistedPanelState;
}

interface PersistedPanelStateV1 {
  readonly activeTabId: string;
  readonly tabs: readonly { readonly id: string; readonly sourceId: string; readonly path: string }[];
  readonly sortColumn: FileSortColumn;
  readonly sortDirection: SortDirection;
  readonly filter: string;
}

interface PersistedCommanderStateV1 {
  readonly version: 1;
  readonly activePanel: PanelSide;
  readonly left: PersistedPanelStateV1;
  readonly right: PersistedPanelStateV1;
}

export const PANEL_STORAGE = new InjectionToken<Storage>('ReachCommander panel storage', {
  providedIn: 'root',
  factory: () => localStorage,
});

@Injectable({ providedIn: 'root' })
export class PanelPersistence {
  // Keep the original key so existing installations can migrate their version-1 payload once.
  static readonly storageKey = 'reachcommander.panel-state.v1';

  constructor(@Inject(PANEL_STORAGE) private readonly storage: Storage) {}

  load(): PersistedCommanderState | null {
    try {
      const value = this.storage.getItem(PanelPersistence.storageKey);
      if (!value) {
        return null;
      }

      const parsed: unknown = JSON.parse(value);
      if (isPersistedCommanderState(parsed)) {
        return parsed;
      }

      return isPersistedCommanderStateV1(parsed) ? migrateV1(parsed) : null;
    } catch {
      return null;
    }
  }

  save(left: PanelState, right: PanelState, activePanel: PanelSide): void {
    const state: PersistedCommanderState = {
      version: 2,
      activePanel,
      left: durablePanel(left),
      right: durablePanel(right),
    };

    try {
      this.storage.setItem(PanelPersistence.storageKey, JSON.stringify(state));
    } catch {
      // Browser storage can be disabled or full; panel operation must still succeed.
    }
  }

  clear(): void {
    try {
      this.storage.removeItem(PanelPersistence.storageKey);
    } catch {
      // Locking must still complete when browser storage is unavailable.
    }
  }
}

function durablePanel(panel: PanelState): PersistedPanelState {
  return {
    activeTabId: panel.activeTabId,
    tabs: panel.tabs.map(({ id, location }) => ({ id, location })),
    sortColumn: panel.sortColumn,
    sortDirection: panel.sortDirection,
    filter: panel.filter,
  };
}

function migrateV1(value: PersistedCommanderStateV1): PersistedCommanderState {
  return {
    version: 2,
    activePanel: value.activePanel,
    left: migratePanelV1(value.left),
    right: migratePanelV1(value.right),
  };
}

function migratePanelV1(value: PersistedPanelStateV1): PersistedPanelState {
  return {
    activeTabId: value.activeTabId,
    tabs: value.tabs.map((tab) => ({
      id: tab.id,
      location: { kind: 'filesystem', sourceId: tab.sourceId, path: tab.path },
    })),
    sortColumn: value.sortColumn,
    sortDirection: value.sortDirection,
    filter: value.filter,
  };
}

function isPersistedCommanderState(value: unknown): value is PersistedCommanderState {
  return isRecord(value) && value['version'] === 2 && isPanelSide(value['activePanel']) &&
    isPersistedPanel(value['left']) && isPersistedPanel(value['right']);
}

function isPersistedCommanderStateV1(value: unknown): value is PersistedCommanderStateV1 {
  return isRecord(value) && value['version'] === 1 && isPanelSide(value['activePanel']) &&
    isPersistedPanelV1(value['left']) && isPersistedPanelV1(value['right']);
}

function hasPanelScalars(value: Record<string, unknown>): boolean {
  return typeof value['activeTabId'] === 'string' && typeof value['filter'] === 'string' &&
    isSortColumn(value['sortColumn']) && isSortDirection(value['sortDirection']) &&
    Array.isArray(value['tabs']);
}

function isPersistedPanel(value: unknown): value is PersistedPanelState {
  if (!isRecord(value) || !hasPanelScalars(value)) {
    return false;
  }

  return (value['tabs'] as unknown[]).every((tab) =>
    isRecord(tab) && typeof tab['id'] === 'string' && isPanelLocation(tab['location']),
  );
}

function isPersistedPanelV1(value: unknown): value is PersistedPanelStateV1 {
  if (!isRecord(value) || !hasPanelScalars(value)) {
    return false;
  }

  return (value['tabs'] as unknown[]).every((tab) =>
    isRecord(tab) && typeof tab['id'] === 'string' &&
    typeof tab['sourceId'] === 'string' && typeof tab['path'] === 'string',
  );
}

function isPanelLocation(value: unknown): value is PanelLocation {
  if (!isRecord(value) || typeof value['sourceId'] !== 'string') {
    return false;
  }

  if (value['kind'] === 'filesystem') {
    return typeof value['path'] === 'string' && value['archivePath'] === undefined &&
      value['internalPath'] === undefined;
  }

  return value['kind'] === 'archive' && typeof value['archivePath'] === 'string' &&
    typeof value['internalPath'] === 'string' && value['path'] === undefined;
}

function isPanelSide(value: unknown): value is PanelSide {
  return value === 'left' || value === 'right';
}

function isSortColumn(value: unknown): value is FileSortColumn {
  return value === 'name' || value === 'extension' || value === 'size' ||
    value === 'modifiedAt' || value === 'attributes';
}

function isSortDirection(value: unknown): value is SortDirection {
  return value === 'ascending' || value === 'descending';
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null;
}
