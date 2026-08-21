import { Injectable, signal } from '@angular/core';
import { ApiProblemDetails, CommanderApiPort, FileEntryDto, SourceDto } from '../api/api.models';
import {
  ArchiveLocation,
  DirectoryTab,
  FilesystemLocation,
  locationParent,
  locationSourceId,
  PanelLocation,
  PanelSide,
  PanelState,
} from './commander.models';
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
  private sessionGeneration = 0;
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
    const location: FilesystemLocation = { kind: 'filesystem', sourceId, path: '/' };
    const tabs = replaceActiveTab(state, this.newTab(source, location, state.activeTabId));
    this.updatePanel(side, resetForNavigation({ ...state, tabs }));
    this.persist();
    await this.loadPanel(side);
  }

  async createTab(side: PanelSide): Promise<void> {
    const state = this.panel(side)();
    const active = activeTab(state);
    if (!active) {
      return;
    }

    const source = this.sourceState().find(
      (candidate) => candidate.id === locationSourceId(active.location),
    );
    if (!source) {
      return;
    }

    const tab = this.newTab(source, active.location);
    this.updatePanel(
      side,
      resetForNavigation({ ...state, tabs: [...state.tabs, tab], activeTabId: tab.id }),
    );
    this.persist();
    await this.loadPanel(side);
  }

  async closeActiveTab(side: PanelSide): Promise<void> {
    const state = this.panel(side)();
    const activeIndex = state.tabs.findIndex((tab) => tab.id === state.activeTabId);
    let tabs = state.tabs.filter((tab) => tab.id !== state.activeTabId);
    if (tabs.length === 0) {
      const current = activeTab(state);
      const source = this.sourceState().find(
        (candidate) => candidate.id === (current ? locationSourceId(current.location) : ''),
      ) ?? this.defaultSource(side);
      tabs = [this.newTab(source, filesystemRoot(source.id))];
    }

    const nextIndex = Math.min(Math.max(activeIndex - 1, 0), tabs.length - 1);
    const next = tabs[nextIndex]!;
    this.updatePanel(
      side,
      resetForNavigation({ ...state, tabs, activeTabId: next.id }),
    );
    this.persist();
    await this.loadPanel(side);
  }

  async activateTab(side: PanelSide, tabId: string): Promise<void> {
    const state = this.panel(side)();
    if (!state.tabs.some((candidate) => candidate.id === tabId)) {
      return;
    }

    this.updatePanel(side, resetForNavigation({ ...state, activeTabId: tabId }));
    this.persist();
    await this.loadPanel(side);
  }

  async navigateTo(side: PanelSide, path: string): Promise<void> {
    const normalized = normalizeLogicalPath(path);
    const state = this.panel(side)();
    const tab = activeTab(state);
    if (!normalized || !tab || tab.location.kind !== 'filesystem') {
      this.updatePanel(side, {
        ...state,
        errorCode: normalized ? 'archive_path_read_only' : 'invalid_path',
        errorDetail: normalized ? 'Archive paths cannot be edited.' : null,
      });
      return;
    }

    await this.navigateToLocation(side, { ...tab.location, path: normalized });
  }

  async navigateArchiveTo(side: PanelSide, internalPath: string): Promise<void> {
    const normalized = normalizeLogicalPath(internalPath);
    const state = this.panel(side)();
    const tab = activeTab(state);
    if (!normalized || !tab || tab.location.kind !== 'archive') {
      return;
    }

    await this.navigateToLocation(side, { ...tab.location, internalPath: normalized });
  }

  async openArchive(side: PanelSide, archivePath: string): Promise<void> {
    const normalizedArchivePath = normalizeLogicalPath(archivePath);
    const state = this.panel(side)();
    const tab = activeTab(state);
    if (!normalizedArchivePath || !tab || tab.location.kind !== 'filesystem') {
      return;
    }

    const source = this.sourceState().find((candidate) => candidate.id === tab.location.sourceId);
    if (!source?.isAvailable) {
      return;
    }

    this.nextRequestToken += 1;
    const requestToken = this.nextRequestToken;
    const origin = tab.location;
    this.updatePanel(side, {
      ...state,
      loading: true,
      errorCode: null,
      errorDetail: null,
      requestToken,
    });

    try {
      const result = await this.api.listArchive(origin.sourceId, normalizedArchivePath, '/');
      const current = this.panel(side)();
      const currentTab = activeTab(current);
      if (current.requestToken !== requestToken || !currentTab ||
          !sameLocation(currentTab.location, origin)) {
        return;
      }

      const location: ArchiveLocation = {
        kind: 'archive',
        sourceId: origin.sourceId,
        archivePath: normalizedArchivePath,
        internalPath: '/',
      };
      const replacement = this.newTab(source, location, currentTab.id);
      const next = {
        ...resetForNavigation({ ...current, tabs: replaceActiveTab(current, replacement) }),
        entries: result.entries,
        loading: false,
        archiveMetadata: { format: result.format, volumeCount: result.volumeCount },
      };
      this.updatePanel(side, {
        ...next,
        cursorIndex: buildVisibleRows(next).length > 0 ? 0 : -1,
      });
      this.persist();
    } catch (error: unknown) {
      const current = this.panel(side)();
      const currentTab = activeTab(current);
      if (current.requestToken === requestToken && currentTab &&
          sameLocation(currentTab.location, origin)) {
        const problem = apiProblem(error);
        this.updatePanel(side, {
          ...current,
          loading: false,
          errorCode: problem.code,
          errorDetail: problem.detail,
        });
      }
    }
  }

  async openEntry(side: PanelSide, entry: FileEntryDto): Promise<void> {
    if ((entry as FileEntryDto & { isParent?: boolean }).isParent) {
      await this.navigateParent(side);
      return;
    }

    const tab = activeTab(this.panel(side)());
    if (!tab) {
      return;
    }

    if (tab.location.kind === 'archive') {
      if (entry.type === 'directory') {
        await this.navigateArchiveTo(side, entry.relativePath);
      }
      return;
    }

    if (entry.type === 'directory') {
      await this.navigateTo(side, entry.relativePath);
    } else if (entry.archiveFormatHint && entry.archiveRole) {
      await this.openArchive(side, entry.relativePath);
    }
  }

  async navigateParent(side: PanelSide): Promise<void> {
    const tab = activeTab(this.panel(side)());
    if (!tab) {
      return;
    }

    await this.navigateToLocation(side, locationParent(tab.location));
  }

  async returnArchiveToParent(side: PanelSide): Promise<void> {
    const tab = activeTab(this.panel(side)());
    if (!tab || tab.location.kind !== 'archive') {
      return;
    }

    const archivePath = tab.location.archivePath;
    await this.navigateToLocation(side, {
      kind: 'filesystem',
      sourceId: tab.location.sourceId,
      path: parentPath(archivePath),
    });
    const current = this.panel(side)();
    const rows = buildVisibleRows(current);
    const cursorIndex = rows.findIndex((row) => row.relativePath === archivePath);
    if (cursorIndex >= 0) {
      this.updatePanel(side, { ...current, cursorIndex });
    }
  }

  refresh(side: PanelSide): Promise<void> {
    return this.loadPanel(side);
  }

  setPathFromEditor(side: PanelSide, path: string): Promise<void> {
    return this.navigateTo(side, path);
  }

  sortBy(side: PanelSide, column: PanelState['sortColumn']): void {
    const state = this.panel(side)();
    const sortDirection = state.sortColumn === column && state.sortDirection === 'ascending'
      ? 'descending'
      : 'ascending';
    this.updatePanel(side, { ...state, sortColumn: column, sortDirection });
    this.persist();
  }

  setFilter(side: PanelSide, filter: string): void {
    const state = { ...this.panel(side)(), filter };
    const rowCount = buildVisibleRows(state).length;
    const cursorIndex = rowCount === 0 ? -1 : Math.min(Math.max(state.cursorIndex, 0), rowCount - 1);
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
      selectedItems.has(row.relativePath)
        ? selectedItems.delete(row.relativePath)
        : selectedItems.add(row.relativePath);
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
      buildVisibleRows(state).filter((row) => !row.isParent).map((row) => row.relativePath),
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
      selectedItems.has(row.relativePath)
        ? selectedItems.delete(row.relativePath)
        : selectedItems.add(row.relativePath);
    } else {
      const anchor = state.selectionAnchor ?? rowIndex;
      selectedItems = new Set(
        rows.slice(Math.min(anchor, rowIndex), Math.max(anchor, rowIndex) + 1)
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
    this.updatePanel(side, { ...state, selectedItems: new Set<string>(), selectionAnchor: null });
  }

  createMultiRenameContext(side: PanelSide): MultiRenameContext | null {
    const panel = this.panel(side)();
    const tab = activeTab(panel);
    if (!tab || tab.location.kind !== 'filesystem') {
      return null;
    }

    const source = this.sourceState().find((candidate) => candidate.id === tab.location.sourceId);
    if (!source) {
      return null;
    }

    const rows = buildVisibleRows(panel);
    const entries = panel.selectedItems.size > 0
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
      directoryPath: tab.location.path,
      entries,
      isAvailable: source.isAvailable,
      isReadOnly: source.isReadOnly,
    };
  }

  initialize(): Promise<void> {
    this.initialization ??= this.initializeCore(this.sessionGeneration);
    return this.initialization;
  }

  reset(): void {
    this.sessionGeneration += 1;
    this.nextRequestToken += 1;
    this.nextTabNumber = 0;
    this.initialization = null;
    this.sourceState.set([]);
    this.leftPanelState.set(emptyPanel());
    this.rightPanelState.set(emptyPanel());
    this.activePanelState.set('left');
    this.persistence.clear();
  }

  private async initializeCore(sessionGeneration: number): Promise<void> {
    const sources = await this.api.getSources();
    if (sessionGeneration !== this.sessionGeneration) {
      return;
    }

    if (sources.length === 0) {
      throw new Error('ReachCommander requires at least one configured source.');
    }

    this.sourceState.set(sources);
    const persisted = this.persistence.load();
    this.leftPanelState.set(this.restorePanel('left', persisted?.left, sources));
    this.rightPanelState.set(this.restorePanel('right', persisted?.right, sources));
    this.activePanelState.set(persisted?.activePanel ?? 'left');
    await Promise.all([this.loadPanel('left'), this.loadPanel('right')]);
    if (sessionGeneration !== this.sessionGeneration) {
      return;
    }

    this.persist();
  }

  private createInitialPanel(side: PanelSide, sources: readonly SourceDto[]): PanelState {
    const source = sources.find((candidate) =>
      side === 'left' ? candidate.defaultLeft : candidate.defaultRight,
    ) ?? sources[0]!;
    const tab = this.newTab(source, filesystemRoot(source.id));
    return { ...emptyPanel(), tabs: [tab], activeTabId: tab.id };
  }

  private newTab(source: SourceDto, location: PanelLocation, id?: string): DirectoryTab {
    if (!id) {
      this.nextTabNumber += 1;
    }

    const path = location.kind === 'filesystem' ? location.path : location.internalPath;
    const fallback = location.kind === 'archive'
      ? location.archivePath.split('/').at(-1) || source.name
      : source.name;
    return {
      id: id ?? `tab-${this.nextTabNumber}`,
      label: path === '/' ? fallback : path.split('/').at(-1) || fallback,
      location,
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
      const source = sourceMap.get(candidate.location.sourceId);
      const location = normalizeLocation(candidate.location);
      if (!source || !location || !candidate.id || ids.has(candidate.id)) {
        continue;
      }

      ids.add(candidate.id);
      tabs.push(this.newTab(source, location, candidate.id));
    }

    if (tabs.length === 0) {
      return this.createInitialPanel(side, sources);
    }

    const active = tabs.find((tab) => tab.id === persisted.activeTabId) ?? tabs[0]!;
    return {
      ...emptyPanel(),
      tabs,
      activeTabId: active.id,
      sortColumn: persisted.sortColumn,
      sortDirection: persisted.sortDirection,
      filter: persisted.filter,
    };
  }

  private defaultSource(side: PanelSide): SourceDto {
    const sources = this.sourceState();
    return sources.find((source) => side === 'left' ? source.defaultLeft : source.defaultRight) ??
      sources[0]!;
  }

  private async navigateToLocation(side: PanelSide, location: PanelLocation): Promise<void> {
    const state = this.panel(side)();
    const tab = activeTab(state);
    const source = this.sourceState().find((candidate) => candidate.id === location.sourceId);
    if (!tab || !source) {
      return;
    }

    const tabs = replaceActiveTab(state, this.newTab(source, location, tab.id));
    this.updatePanel(side, resetForNavigation({ ...state, tabs }));
    this.persist();
    await this.loadPanel(side);
  }

  private async loadPanel(side: PanelSide): Promise<void> {
    const state = this.panel(side)();
    const tab = activeTab(state);
    if (!tab) {
      return;
    }

    this.nextRequestToken += 1;
    const requestToken = this.nextRequestToken;
    const requestedLocation = tab.location;
    const source = this.sourceState().find((candidate) => candidate.id === requestedLocation.sourceId);
    if (!source?.isAvailable) {
      this.updatePanel(side, {
        ...state,
        loading: false,
        entries: [],
        errorCode: 'source_unavailable',
        errorDetail: 'This source is currently unavailable.',
        archiveMetadata: null,
        requestToken,
      });
      return;
    }

    this.updatePanel(side, {
      ...state,
      loading: true,
      errorCode: null,
      errorDetail: null,
      archiveMetadata: null,
      requestToken,
    });

    try {
      const result = requestedLocation.kind === 'filesystem'
        ? { entries: await this.api.listFiles(requestedLocation.sourceId, requestedLocation.path), metadata: null }
        : await this.loadArchive(requestedLocation);
      const current = this.panel(side)();
      const currentTab = activeTab(current);
      if (current.requestToken !== requestToken || !currentTab ||
          !sameLocation(currentTab.location, requestedLocation)) {
        return;
      }

      const next = {
        ...current,
        entries: result.entries,
        loading: false,
        errorCode: null,
        errorDetail: null,
        archiveMetadata: result.metadata,
      };
      this.updatePanel(side, {
        ...next,
        cursorIndex: buildVisibleRows(next).length > 0 ? 0 : -1,
      });
    } catch (error: unknown) {
      const current = this.panel(side)();
      const currentTab = activeTab(current);
      if (current.requestToken === requestToken && currentTab &&
          sameLocation(currentTab.location, requestedLocation)) {
        const problem = apiProblem(error);
        this.updatePanel(side, {
          ...current,
          entries: [],
          loading: false,
          errorCode: problem.code,
          errorDetail: problem.detail,
          archiveMetadata: null,
          cursorIndex: -1,
        });
      }
    }
  }

  private async loadArchive(location: ArchiveLocation) {
    const result = await this.api.listArchive(
      location.sourceId,
      location.archivePath,
      location.internalPath,
    );
    return {
      entries: result.entries,
      metadata: { format: result.format, volumeCount: result.volumeCount },
    };
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

function activeTab(state: PanelState): DirectoryTab | undefined {
  return state.tabs.find((tab) => tab.id === state.activeTabId);
}

function replaceActiveTab(state: PanelState, replacement: DirectoryTab): readonly DirectoryTab[] {
  return state.tabs.map((tab) => tab.id === state.activeTabId ? replacement : tab);
}

function filesystemRoot(sourceId: string): FilesystemLocation {
  return { kind: 'filesystem', sourceId, path: '/' };
}

function normalizeLocation(location: PanelLocation): PanelLocation | null {
  if (location.kind === 'filesystem') {
    const path = normalizeLogicalPath(location.path);
    return path ? { kind: 'filesystem', sourceId: location.sourceId, path } : null;
  }

  const archivePath = normalizeLogicalPath(location.archivePath);
  const internalPath = normalizeLogicalPath(location.internalPath);
  return archivePath && internalPath
    ? { kind: 'archive', sourceId: location.sourceId, archivePath, internalPath }
    : null;
}

function sameLocation(left: PanelLocation, right: PanelLocation): boolean {
  return left.kind === right.kind && left.sourceId === right.sourceId &&
    (left.kind === 'filesystem' && right.kind === 'filesystem'
      ? left.path === right.path
      : left.kind === 'archive' && right.kind === 'archive' &&
        left.archivePath === right.archivePath && left.internalPath === right.internalPath);
}

function parentPath(path: string): string {
  return parentLogicalPath(path);
}

function apiProblem(error: unknown): Pick<ApiProblemDetails, 'code' | 'detail'> {
  if (isRecord(error)) {
    const body = isRecord(error['error']) ? error['error'] : error;
    if (typeof body['code'] === 'string') {
      return {
        code: body['code'],
        detail: typeof body['detail'] === 'string' ? body['detail'] : 'The location request failed.',
      };
    }
  }

  return { code: 'request_failed', detail: 'The request could not be completed.' };
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null;
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
    errorDetail: null,
    archiveMetadata: null,
  };
}

function emptyPanel(): PanelState {
  return {
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
    errorDetail: null,
    archiveMetadata: null,
    requestToken: 0,
  };
}
