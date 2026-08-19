import {
  CommanderApiPort,
  FileEntryDto,
  SourceDto,
  SystemMetricsDto,
} from '../api/api.models';
import { CommanderStore } from './commander-store';

describe('CommanderStore', () => {
  beforeEach(() => localStorage.clear());

  it('initializes panes from independent configured defaults', async () => {
    const api = new FakeCommanderApi([
      source('downloads', { defaultLeft: true }),
      source('media', { defaultRight: true }),
    ]);
    const store = new CommanderStore(api);

    await store.initialize();

    expect(store.leftPanel().sourceId).toBe('downloads');
    expect(store.rightPanel().sourceId).toBe('media');
    expect(store.leftPanel().tabs[0]?.path).toBe('/');
    expect(store.rightPanel().tabs[0]?.path).toBe('/');
    expect(store.activePanel()).toBe('left');
  });

  it('keeps an unavailable configured default selected without requesting it', async () => {
    const api = new FakeCommanderApi([
      source('downloads', { defaultLeft: true }),
      source('usb', { defaultRight: true, isAvailable: false }),
    ]);
    const store = new CommanderStore(api);

    await store.initialize();

    expect(store.rightPanel().sourceId).toBe('usb');
    expect(store.rightPanel().errorCode).toBe('source_unavailable');
    expect(api.listRequests).toEqual([{ sourceId: 'downloads', path: '/' }]);
  });

  it('loads each available pane independently', async () => {
    const api = new FakeCommanderApi([
      source('downloads', { defaultLeft: true }),
      source('media', { defaultRight: true }),
    ]);
    api.entries.set('downloads:/', [entry('left.txt')]);
    api.entries.set('media:/', [entry('right.txt')]);
    const store = new CommanderStore(api);

    await store.initialize();

    expect(store.leftPanel().entries.map((item) => item.name)).toEqual(['left.txt']);
    expect(store.rightPanel().entries.map((item) => item.name)).toEqual(['right.txt']);
  });

  it('changes only the requested panel source and active tab', async () => {
    const api = new FakeCommanderApi([
      source('downloads', { defaultLeft: true }),
      source('media', { defaultRight: true }),
    ]);
    const store = new CommanderStore(api);
    await store.initialize();
    const rightBefore = store.rightPanel();

    await store.selectSource('left', 'media');

    expect(store.leftPanel().sourceId).toBe('media');
    expect(store.leftPanel().tabs[0]?.path).toBe('/');
    expect(store.rightPanel()).toBe(rightBefore);
  });

  it('creates, switches, and closes tabs with source and path memory', async () => {
    const api = new FakeCommanderApi([
      source('downloads', { defaultLeft: true }),
      source('media', { defaultRight: true }),
    ]);
    const store = new CommanderStore(api);
    await store.initialize();
    await store.navigateTo('left', '/Complete');
    const firstTabId = store.leftPanel().activeTabId;

    await store.createTab('left');
    await store.selectSource('left', 'media');
    const secondTabId = store.leftPanel().activeTabId;
    await store.activateTab('left', firstTabId);

    expect(store.leftPanel().sourceId).toBe('downloads');
    expect(store.leftPanel().tabs.find((tab) => tab.id === firstTabId)?.path).toBe('/Complete');
    expect(store.leftPanel().tabs.find((tab) => tab.id === secondTabId)?.sourceId).toBe('media');

    await store.closeActiveTab('left');
    expect(store.leftPanel().tabs).toHaveLength(1);
    expect(store.leftPanel().activeTabId).toBe(secondTabId);
  });

  it('replaces the final closed tab with a safe root tab', async () => {
    const api = new FakeCommanderApi([source('downloads', { defaultLeft: true, defaultRight: true })]);
    const store = new CommanderStore(api);
    await store.initialize();

    await store.closeActiveTab('left');

    expect(store.leftPanel().tabs).toHaveLength(1);
    expect(store.leftPanel().tabs[0]?.path).toBe('/');
  });

  it('ignores a stale navigation response', async () => {
    const api = new FakeCommanderApi([source('downloads', { defaultLeft: true, defaultRight: true })]);
    const store = new CommanderStore(api);
    await store.initialize();
    const slow = deferred<readonly FileEntryDto[]>();
    api.listHandler = (_sourceId, path) =>
      path === '/Slow' ? slow.promise : Promise.resolve([entry('fast.txt')]);

    const slowNavigation = store.navigateTo('left', '/Slow');
    await store.navigateTo('left', '/Fast');
    slow.resolve([entry('slow.txt')]);
    await slowNavigation;

    expect(store.leftPanel().tabs[0]?.path).toBe('/Fast');
    expect(store.leftPanel().entries.map((item) => item.name)).toEqual(['fast.txt']);
  });

  it('repairs persisted tabs whose sources were removed', async () => {
    localStorage.setItem('reachcommander.panel-state.v1', JSON.stringify({
      version: 1,
      activePanel: 'right',
      left: persistedPanel('removed', '/Lost'),
      right: persistedPanel('media', '/Movies'),
    }));
    const api = new FakeCommanderApi([
      source('downloads', { defaultLeft: true }),
      source('media', { defaultRight: true }),
    ]);
    const store = new CommanderStore(api);

    await store.initialize();

    expect(store.leftPanel().sourceId).toBe('downloads');
    expect(store.leftPanel().tabs[0]?.path).toBe('/');
    expect(store.rightPanel().sourceId).toBe('media');
    expect(store.rightPanel().tabs[0]?.path).toBe('/Movies');
    expect(store.activePanel()).toBe('right');
  });

  it('toggles selection with Insert and advances the cursor', async () => {
    const api = new FakeCommanderApi([source('downloads', { defaultLeft: true, defaultRight: true })]);
    api.entries.set('downloads:/', [entry('one.txt'), entry('two.txt')]);
    const store = new CommanderStore(api);
    await store.initialize();

    store.toggleCursorSelection('left');

    expect([...store.leftPanel().selectedItems]).toEqual(['/one.txt']);
    expect(store.leftPanel().cursorIndex).toBe(1);
  });

  it('selects all visible real entries and excludes the parent row', async () => {
    const api = new FakeCommanderApi([source('downloads', { defaultLeft: true, defaultRight: true })]);
    api.entries.set('downloads:/Complete', [entry('one.txt'), entry('two.txt')]);
    const store = new CommanderStore(api);
    await store.initialize();
    await store.navigateTo('left', '/Complete');

    store.selectAllVisible('left');

    expect([...store.leftPanel().selectedItems].sort()).toEqual(['/one.txt', '/two.txt']);
  });

  it('supports plain, toggle, and range pointer selection', async () => {
    const api = new FakeCommanderApi([source('downloads', { defaultLeft: true, defaultRight: true })]);
    api.entries.set('downloads:/', [entry('alpha.txt'), entry('beta.txt'), entry('gamma.txt')]);
    const store = new CommanderStore(api);
    await store.initialize();

    store.selectWithPointer('left', 0, 'replace');
    store.selectWithPointer('left', 2, 'range');
    expect([...store.leftPanel().selectedItems].sort()).toEqual(['/alpha.txt', '/beta.txt', '/gamma.txt']);

    store.selectWithPointer('left', 1, 'toggle');
    expect(store.leftPanel().selectedItems.has('/beta.txt')).toBe(false);
  });

  it('clamps the cursor after filtering and keeps the opposite pane untouched', async () => {
    const api = new FakeCommanderApi([source('downloads', { defaultLeft: true, defaultRight: true })]);
    api.entries.set('downloads:/', [entry('alpha.txt'), entry('beta.txt')]);
    const store = new CommanderStore(api);
    await store.initialize();
    store.moveCursorBoundary('left', 'end');
    const rightBefore = store.rightPanel();

    store.setFilter('left', 'alpha');

    expect(store.leftPanel().cursorIndex).toBe(0);
    expect(store.rightPanel()).toBe(rightBefore);
  });
});

function persistedPanel(sourceId: string, path: string) {
  return {
    activeTabId: 'persisted-tab',
    tabs: [{ id: 'persisted-tab', label: 'Persisted', sourceId, path }],
    sortColumn: 'name',
    sortDirection: 'ascending',
    filter: '',
  };
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((complete) => {
    resolve = complete;
  });
  return { promise, resolve };
}

function source(
  id: string,
  overrides: Partial<SourceDto> = {},
): SourceDto {
  return {
    id,
    name: id[0]?.toUpperCase() + id.slice(1),
    isAvailable: true,
    isReadOnly: false,
    totalBytes: 1000,
    usedBytes: 250,
    freeBytes: 750,
    defaultLeft: false,
    defaultRight: false,
    ...overrides,
  };
}

function entry(name: string): FileEntryDto {
  return {
    name,
    relativePath: `/${name}`,
    type: 'file',
    size: 1,
    modifiedAt: '2026-08-19T10:00:00Z',
    extension: 'txt',
    isReadOnly: false,
    isSymbolicLink: false,
    attributes: 'Normal',
  };
}

class FakeCommanderApi extends CommanderApiPort {
  readonly entries = new Map<string, readonly FileEntryDto[]>();
  readonly listRequests: { sourceId: string; path: string }[] = [];
  listHandler: ((sourceId: string, path: string) => Promise<readonly FileEntryDto[]>) | null = null;

  constructor(private readonly configuredSources: readonly SourceDto[]) {
    super();
  }

  async getSystemMetrics(): Promise<SystemMetricsDto> {
    return {
      sampledAt: new Date().toISOString(),
      state: 'disabled',
      hostUptimeSeconds: null,
      cpu: null,
      memory: null,
      storage: [],
      gpus: [],
      fans: [],
      network: null,
      collectors: [],
    };
  }

  async getSources(): Promise<readonly SourceDto[]> {
    return this.configuredSources;
  }

  async listFiles(sourceId: string, path: string): Promise<readonly FileEntryDto[]> {
    this.listRequests.push({ sourceId, path });
    if (this.listHandler) {
      return this.listHandler(sourceId, path);
    }
    return this.entries.get(`${sourceId}:${path}`) ?? [];
  }

  async getInfo(): Promise<FileEntryDto> {
    throw new Error('Not used by these tests');
  }
}
