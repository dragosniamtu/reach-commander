import { Injectable, signal } from '@angular/core';
import { CommanderApiPort, SourceDto } from '../api/api.models';
import { DirectoryTab, PanelSide, PanelState } from './commander.models';
import { PanelPersistence, PersistedPanelState } from './panel-persistence';
import { normalizeLogicalPath, parentLogicalPath } from './path-utils';
import { buildVisibleRows } from './file-table.viewmodel';
import { MultiRenameContext } from './multi-rename.models';

@Injectable({ providedIn: 'root' })
export class CommanderStore {
  private readonly sourceState = signal<readonly SourceDto[]>([]);
  private readonly leftPanelState = signal<PanelState>(emptyPanel());
  private readonly rightPanelState = signal<PanelState>(emptyPanel());
  private readonly activePanelState = signal<PanelSide>('left');
  private nextTabNumber = 0;
  private nextRequestToken = 0;
  private initialization: Promise<void> | null = null;

  readonly sources = this.sourceState.asReadonly();
  readonly leftPanel = this.leftPanelState.asReadonly();
  readonly rightPanel = this.rightPanelState.asReadonly();
  readonly activePanel = this.activePanelState.asReadonly();

  constructor(
    private readonly api: CommanderApiPort,
    private readonly persistence: PanelPersistence = new PanelPersistence(localStorage),
  ) {}

  activatePanel(side: PanelSide): void {
    this.activePanelState.set(side);
    this.persist();
  }

  async selectSource(side: PanelSide, sourceId: string): Promise<void> {
    const source = this.sourceState().find((candidate) => candidate.id === sourceId);
    if (!source) {
      return;
    }

    const state = this.panel(side)();
    const tabs = state.tabs.map((tab) =>
      tab.id === state.activeTabId ? this.newTab(source, '/', tab.id) : tab,
    );
    this.updatePanel(side, resetForNavigation({ ...state, sourceId, tabs }));
    this.persist();
    await this.loadPanel(side);
  }

  async createTab(side: PanelSide): Promise<void> {
    const state = this.panel(side)();
    const active = state.tabs.find((tab) => tab.id === state.activeTabId)!;
    const source = this.sourceState().find((candidate) => candidate.id === active.sourceId)!;
    const tab = this.newTab(source, active.path);
    this.updatePanel(
      side,
      resetForNavigation({
        ...state,
        sourceId: tab.sourceId,
        tabs: [...state.tabs, tab],
        activeTabId: tab.id,
      }),
    );
    this.persist();
    await this.loadPanel(side);
  }

  async closeActiveTab(side: PanelSide): Promise<void> {
    const state = this.panel(side)();
    const activeIndex = state.tabs.findIndex((tab) => tab.id === state.activeTabId);
    let tabs = state.tabs.filter((tab) => tab.id !== state.activeTabId);
    if (tabs.length === 0) {
      const source =
        this.sourceState().find((candidate) => candidate.id === state.sourceId) ??
        this.defaultSource(side);
      tabs = [this.newTab(source, '/')];
    }

    const nextIndex = Math.min(Math.max(activeIndex - 1, 0), tabs.length - 1);
    const active = tabs[nextIndex]!;
    this.updatePanel(
      side,
      resetForNavigation({
        ...state,
        sourceId: active.sourceId,
        tabs,
        activeTabId: active.id,
      }),
    );
    this.persist();
    await this.loadPanel(side);
  }

  async activateTab(side: PanelSide, tabId: string): Promise<void> {
    const state = this.panel(side)();
    const tab = state.tabs.find((candidate) => candidate.id === tabId);
    if (!tab) {
      return;
    }

    this.updatePanel(
      side,
      resetForNavigation({
        ...state,
        sourceId: tab.sourceId,
        activeTabId: tab.id,
      }),
    );
    this.persist();
    await this.loadPanel(side);
  }

  async navigateTo(side: PanelSide, path: string): Promise<void> {
    const normalized = normalizeLogicalPath(path);
    const state = this.panel(side)();
    if (!normalized) {
      this.updatePanel(side, { ...state, errorCode: 'invalid_path' });
      return;
    }

    const source = this.sourceState().find((candidate) => candidate.id === state.sourceId)!;
    const tabs = state.tabs.map((tab) =>
      tab.id === state.activeTabId ? this.newTab(source, normalized, tab.id) : tab,
    );
    this.updatePanel(side, resetForNavigation({ ...state, tabs }));
    this.persist();
    await this.loadPanel(side);
  }

  navigateParent(side: PanelSide): Promise<void> {
    const state = this.panel(side)();
    const active = state.tabs.find((tab) => tab.id === state.activeTabId)!;
    return this.navigateTo(side, parentLogicalPath(active.path));
  }

  refresh(side: PanelSide): Promise<void> {
    return this.loadPanel(side);
  }

  setPathFromEditor(side: PanelSide, path: string): Promise<void> {
    return this.navigateTo(side, path);
  }

  sortBy(side: PanelSide, column: PanelState['sortColumn']): void {
    const state = this.panel(side)();
    const sortDirection =
      state.sortColumn === column && state.sortDirection === 'ascending'
        ? 'descending'
        : 'ascending';
    this.updatePanel(side, { ...state, sortColumn: column, sortDirection });
    this.persist();
  }

  setFilter(side: PanelSide, filter: string): void {
    const state = { ...this.panel(side)(), filter };
    const rowCount = buildVisibleRows(state).length;
    const cursorIndex =
      rowCount === 0 ? -1 : Math.min(Math.max(state.cursorIndex, 0), rowCount - 1);
    this.updatePanel(side, { ...state, cursorIndex });
    this.persist();
  }

  moveCursor(side: PanelSide, amount: number): void {
    const state = this.panel(side)();
    const rowCount = buildVisibleRows(state).length;
    if (rowCount === 0) {
      this.updatePanel(side, { ...state, cursorIndex: -1 });
      return;
    }

    const cursorIndex = Math.min(
      Math.max((state.cursorIndex < 0 ? 0 : state.cursorIndex) + amount, 0),
      rowCount - 1,
    );
    this.updatePanel(side, { ...state, cursorIndex });
  }

  moveCursorPage(side: PanelSide, direction: -1 | 1): void {
    this.moveCursor(side, direction * 10);
  }

  moveCursorBoundary(side: PanelSide, boundary: 'home' | 'end'): void {
    const state = this.panel(side)();
    const rowCount = buildVisibleRows(state).length;
    this.updatePanel(side, {
      ...state,
      cursorIndex: rowCount === 0 ? -1 : boundary === 'home' ? 0 : rowCount - 1,
    });
  }

  toggleCursorSelection(side: PanelSide): void {
    const state = this.panel(side)();
    const rows = buildVisibleRows(state);
    const row = rows[state.cursorIndex];
    if (!row) {
      return;
    }

    const selectedItems = new Set(state.selectedItems);
    if (!row.isParent) {
      if (selectedItems.has(row.relativePath)) {
        selectedItems.delete(row.relativePath);
      } else {
        selectedItems.add(row.relativePath);
      }
    }

    this.updatePanel(side, {
      ...state,
      selectedItems,
      selectionAnchor: row.isParent ? state.selectionAnchor : state.cursorIndex,
      cursorIndex: Math.min(state.cursorIndex + 1, rows.length - 1),
    });
  }

  selectAllVisible(side: PanelSide): void {
    const state = this.panel(side)();
    const selectedItems = new Set(
      buildVisibleRows(state)
        .filter((row) => !row.isParent)
        .map((row) => row.relativePath),
    );
    this.updatePanel(side, { ...state, selectedItems });
  }

  selectWithPointer(side: PanelSide, rowIndex: number, mode: 'replace' | 'toggle' | 'range'): void {
    const state = this.panel(side)();
    const rows = buildVisibleRows(state);
    const row = rows[rowIndex];
    if (!row) {
      return;
    }

    if (row.isParent) {
      this.updatePanel(side, { ...state, cursorIndex: rowIndex });
      return;
    }

    let selectedItems = new Set(state.selectedItems);
    if (mode === 'replace') {
      selectedItems = new Set([row.relativePath]);
    } else if (mode === 'toggle') {
      if (selectedItems.has(row.relativePath)) {
        selectedItems.delete(row.relativePath);
      } else {
        selectedItems.add(row.relativePath);
      }
    } else {
      const anchor = state.selectionAnchor ?? rowIndex;
      const start = Math.min(anchor, rowIndex);
      const end = Math.max(anchor, rowIndex);
      selectedItems = new Set(
        rows
          .slice(start, end + 1)
          .filter((candidate) => !candidate.isParent)
          .map((candidate) => candidate.relativePath),
      );
    }

    this.updatePanel(side, {
      ...state,
      cursorIndex: rowIndex,
      selectedItems,
      selectionAnchor: mode === 'range' ? (state.selectionAnchor ?? rowIndex) : rowIndex,
    });
  }

  clearSelection(side: PanelSide): void {
    const state = this.panel(side)();
    this.updatePanel(side, {
      ...state,
      selectedItems: new Set<string>(),
      selectionAnchor: null,
    });
  }

  createMultiRenameContext(side: PanelSide): MultiRenameContext | null {
    const panel = this.panel(side)();
    const activeTab = panel.tabs.find((tab) => tab.id === panel.activeTabId);
    const source = this.sourceState().find((candidate) => candidate.id === activeTab?.sourceId);
    if (!activeTab || !source) {
      return null;
    }

    const rows = buildVisibleRows(panel);
    const entries =
      panel.selectedItems.size > 0
        ? rows.filter((row) => !row.isParent && panel.selectedItems.has(row.relativePath))
        : rows[panel.cursorIndex] && !rows[panel.cursorIndex]!.isParent
          ? [rows[panel.cursorIndex]!]
          : [];
    if (entries.length === 0) {
      return null;
    }

    return {
      panelSide: side,
      sourceId: source.id,
      sourceName: source.name,
      directoryPath: activeTab.path,
      entries,
      isAvailable: source.isAvailable,
      isReadOnly: source.isReadOnly,
    };
  }

  initialize(): Promise<void> {
    this.initialization ??= this.initializeCore();
    return this.initialization;
  }

  private async initializeCore(): Promise<void> {
    const sources = await this.api.getSources();
    if (sources.length === 0) {
      throw new Error('ReachCommander requires at least one configured source.');
    }

    this.sourceState.set(sources);
    const persisted = this.persistence.load();
    this.leftPanelState.set(this.restorePanel('left', persisted?.left, sources));
    this.rightPanelState.set(this.restorePanel('right', persisted?.right, sources));
    this.activePanelState.set(persisted?.activePanel ?? 'left');
    await Promise.all([this.loadPanel('left'), this.loadPanel('right')]);
    this.persist();
  }

  private createInitialPanel(side: PanelSide, sources: readonly SourceDto[]): PanelState {
    const source =
      sources.find((candidate) =>
        side === 'left' ? candidate.defaultLeft : candidate.defaultRight,
      ) ?? sources[0]!;
    const tab = this.newTab(source, '/');
    return {
      ...emptyPanel(),
      sourceId: source.id,
      tabs: [tab],
      activeTabId: tab.id,
    };
  }

  private newTab(source: SourceDto, path: string, id?: string): DirectoryTab {
    if (!id) {
      this.nextTabNumber += 1;
    }
    return {
      id: id ?? `tab-${this.nextTabNumber}`,
      label: path === '/' ? source.name : path.split('/').at(-1) || source.name,
      sourceId: source.id,
      path,
    };
  }

  private restorePanel(
    side: PanelSide,
    persisted: PersistedPanelState | undefined,
    sources: readonly SourceDto[],
  ): PanelState {
    if (!persisted) {
      return this.createInitialPanel(side, sources);
    }

    const sourceMap = new Map(sources.map((source) => [source.id, source]));
    const ids = new Set<string>();
    const tabs: DirectoryTab[] = [];
    for (const candidate of persisted.tabs) {
      const source = sourceMap.get(candidate.sourceId);
      const path = normalizeLogicalPath(candidate.path);
      if (!source || !path || !candidate.id || ids.has(candidate.id)) {
        continue;
      }

      ids.add(candidate.id);
      tabs.push(this.newTab(source, path, candidate.id));
    }

    if (tabs.length === 0) {
      return this.createInitialPanel(side, sources);
    }

    const active = tabs.find((tab) => tab.id === persisted.activeTabId) ?? tabs[0]!;
    return {
      ...emptyPanel(),
      sourceId: active.sourceId,
      tabs,
      activeTabId: active.id,
      sortColumn: persisted.sortColumn,
      sortDirection: persisted.sortDirection,
      filter: persisted.filter,
    };
  }

  private defaultSource(side: PanelSide): SourceDto {
    const sources = this.sourceState();
    return (
      sources.find((source) => (side === 'left' ? source.defaultLeft : source.defaultRight)) ??
      sources[0]!
    );
  }

  private async loadPanel(side: PanelSide): Promise<void> {
    const state = this.panel(side)();
    const source = this.sourceState().find((candidate) => candidate.id === state.sourceId);
    if (!source?.isAvailable) {
      this.updatePanel(side, {
        ...state,
        loading: false,
        entries: [],
        errorCode: 'source_unavailable',
      });
      return;
    }

    const activeTab = state.tabs.find((tab) => tab.id === state.activeTabId)!;
    this.nextRequestToken += 1;
    const requestToken = this.nextRequestToken;
    this.updatePanel(side, {
      ...state,
      loading: true,
      errorCode: null,
      requestToken,
    });

    try {
      const entries = await this.api.listFiles(activeTab.sourceId, activeTab.path);
      const current = this.panel(side)();
      const currentTab = current.tabs.find((tab) => tab.id === current.activeTabId);
      if (
        current.requestToken !== requestToken ||
        currentTab?.sourceId !== activeTab.sourceId ||
        currentTab.path !== activeTab.path
      ) {
        return;
      }

      this.updatePanel(side, {
        ...current,
        entries,
        loading: false,
        errorCode: null,
        cursorIndex: buildVisibleRows({ ...current, entries }).length > 0 ? 0 : -1,
      });
    } catch {
      const current = this.panel(side)();
      if (current.requestToken === requestToken) {
        this.updatePanel(side, {
          ...current,
          entries: [],
          loading: false,
          errorCode: 'request_failed',
          cursorIndex: -1,
        });
      }
    }
  }

  private panel(side: PanelSide) {
    return side === 'left' ? this.leftPanelState : this.rightPanelState;
  }

  private updatePanel(side: PanelSide, state: PanelState): void {
    this.panel(side).set(state);
  }

  private persist(): void {
    this.persistence.save(this.leftPanelState(), this.rightPanelState(), this.activePanelState());
  }
}

function resetForNavigation(state: PanelState): PanelState {
  return {
    ...state,
    cursorIndex: -1,
    selectedItems: new Set<string>(),
    selectionAnchor: null,
    entries: [],
    loading: false,
    errorCode: null,
  };
}

function emptyPanel(): PanelState {
  return {
    sourceId: '',
    tabs: [],
    activeTabId: '',
    cursorIndex: -1,
    selectedItems: new Set<string>(),
    selectionAnchor: null,
    sortColumn: 'name',
    sortDirection: 'ascending',
    filter: '',
    entries: [],
    loading: false,
    errorCode: null,
    requestToken: 0,
  };
}
