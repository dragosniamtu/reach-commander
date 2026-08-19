import { Inject, Injectable, InjectionToken } from '@angular/core';
import { DirectoryTab, FileSortColumn, PanelSide, PanelState, SortDirection } from './commander.models';

export interface PersistedPanelState {
  readonly activeTabId: string;
  readonly tabs: readonly Pick<DirectoryTab, 'id' | 'sourceId' | 'path'>[];
  readonly sortColumn: FileSortColumn;
  readonly sortDirection: SortDirection;
  readonly filter: string;
}

export interface PersistedCommanderState {
  readonly version: 1;
  readonly activePanel: PanelSide;
  readonly left: PersistedPanelState;
  readonly right: PersistedPanelState;
}

export const PANEL_STORAGE = new InjectionToken<Storage>('ReachCommander panel storage', {
  providedIn: 'root',
  factory: () => localStorage,
});

@Injectable({ providedIn: 'root' })
export class PanelPersistence {
  static readonly storageKey = 'reachcommander.panel-state.v1';

  constructor(@Inject(PANEL_STORAGE) private readonly storage: Storage) {}

  load(): PersistedCommanderState | null {
    try {
      const value = this.storage.getItem(PanelPersistence.storageKey);
      if (!value) {
        return null;
      }

      const parsed: unknown = JSON.parse(value);
      return isPersistedCommanderState(parsed) ? parsed : null;
    } catch {
      return null;
    }
  }

  save(left: PanelState, right: PanelState, activePanel: PanelSide): void {
    const state: PersistedCommanderState = {
      version: 1,
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
}

function durablePanel(panel: PanelState): PersistedPanelState {
  return {
    activeTabId: panel.activeTabId,
    tabs: panel.tabs.map(({ id, sourceId, path }) => ({ id, sourceId, path })),
    sortColumn: panel.sortColumn,
    sortDirection: panel.sortDirection,
    filter: panel.filter,
  };
}

function isPersistedCommanderState(value: unknown): value is PersistedCommanderState {
  if (!isRecord(value) || value['version'] !== 1 ||
      (value['activePanel'] !== 'left' && value['activePanel'] !== 'right')) {
    return false;
  }

  return isPersistedPanel(value['left']) && isPersistedPanel(value['right']);
}

function isPersistedPanel(value: unknown): value is PersistedPanelState {
  if (!isRecord(value) || typeof value['activeTabId'] !== 'string' ||
      typeof value['filter'] !== 'string' ||
      !isSortColumn(value['sortColumn']) ||
      (value['sortDirection'] !== 'ascending' && value['sortDirection'] !== 'descending') ||
      !Array.isArray(value['tabs'])) {
    return false;
  }

  return value['tabs'].every((tab) =>
    isRecord(tab) &&
    typeof tab['id'] === 'string' &&
    typeof tab['sourceId'] === 'string' &&
    typeof tab['path'] === 'string',
  );
}

function isSortColumn(value: unknown): value is FileSortColumn {
  return value === 'name' || value === 'extension' || value === 'size' ||
    value === 'modifiedAt' || value === 'attributes';
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null;
}
