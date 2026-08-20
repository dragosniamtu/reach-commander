import {
  ArchiveDirectoryDto,
  CommanderApiPort,
  FileEntryDto,
  SourceDto,
  SystemMetricsDto,
  UploadEvent,
  UploadLimitsDto,
} from '../api/api.models';
import { EMPTY, Observable } from 'rxjs';
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

    expect(store.leftPanel().tabs[0]?.location).toEqual({
      kind: 'filesystem', sourceId: 'downloads', path: '/',
    });
    expect(store.rightPanel().tabs[0]?.location).toEqual({
      kind: 'filesystem', sourceId: 'media', path: '/',
    });
    expect(store.activePanel()).toBe('left');
  });

  it('keeps an unavailable configured default selected without requesting it', async () => {
    const api = new FakeCommanderApi([
      source('downloads', { defaultLeft: true }),
      source('usb', { defaultRight: true, isAvailable: false }),
    ]);
    const store = new CommanderStore(api);

    await store.initialize();

    expect(store.rightPanel().tabs[0]?.location.sourceId).toBe('usb');
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

    expect(store.leftPanel().tabs[0]?.location).toEqual({
      kind: 'filesystem', sourceId: 'media', path: '/',
    });
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

    expect(store.leftPanel().tabs.find((tab) => tab.id === firstTabId)?.location).toEqual({
      kind: 'filesystem', sourceId: 'downloads', path: '/Complete',
    });
    expect(store.leftPanel().tabs.find((tab) => tab.id === secondTabId)?.location.sourceId)
      .toBe('media');

    await store.closeActiveTab('left');
    expect(store.leftPanel().tabs).toHaveLength(1);
    expect(store.leftPanel().activeTabId).toBe(secondTabId);
  });

  it('replaces the final closed tab with a safe root tab', async () => {
    const api = new FakeCommanderApi([
      source('downloads', { defaultLeft: true, defaultRight: true }),
    ]);
    const store = new CommanderStore(api);
    await store.initialize();

    await store.closeActiveTab('left');

    expect(store.leftPanel().tabs).toHaveLength(1);
    expect(store.leftPanel().tabs[0]?.location).toEqual({
      kind: 'filesystem', sourceId: 'downloads', path: '/',
    });
  });

  it('ignores a stale navigation response', async () => {
    const api = new FakeCommanderApi([
      source('downloads', { defaultLeft: true, defaultRight: true }),
    ]);
    const store = new CommanderStore(api);
    await store.initialize();
    const slow = deferred<readonly FileEntryDto[]>();
    api.listHandler = (_sourceId, path) =>
      path === '/Slow' ? slow.promise : Promise.resolve([entry('fast.txt')]);

    const slowNavigation = store.navigateTo('left', '/Slow');
    await store.navigateTo('left', '/Fast');
    slow.resolve([entry('slow.txt')]);
    await slowNavigation;

    expect(store.leftPanel().tabs[0]?.location).toEqual({
      kind: 'filesystem', sourceId: 'downloads', path: '/Fast',
    });
    expect(store.leftPanel().entries.map((item) => item.name)).toEqual(['fast.txt']);
  });

  it('ignores a stale navigation rejection after switching to an unavailable source', async () => {
    const api = new FakeCommanderApi([
      source('downloads', { defaultLeft: true, defaultRight: true }),
      source('usb', { isAvailable: false }),
    ]);
    const store = new CommanderStore(api);
    await store.initialize();
    const slow = deferred<readonly FileEntryDto[]>();
    api.listHandler = (_sourceId, path) =>
      path === '/Slow' ? slow.promise : Promise.resolve([]);

    const navigation = store.navigateTo('left', '/Slow');
    await store.selectSource('left', 'usb');
    slow.reject({ error: { code: 'old_failure', detail: 'Old request failed.' } });
    await navigation;

    expect(store.leftPanel().tabs[0]?.location).toEqual({
      kind: 'filesystem', sourceId: 'usb', path: '/',
    });
    expect(store.leftPanel().errorCode).toBe('source_unavailable');
  });

  it('ignores a stale archive-open rejection after switching source', async () => {
    const api = new FakeCommanderApi([
      source('downloads', { defaultLeft: true, defaultRight: true }),
      source('usb', { isAvailable: false }),
    ]);
    const store = new CommanderStore(api);
    await store.initialize();
    const slow = deferred<ArchiveDirectoryDto>();
    api.archiveHandler = () => slow.promise;

    const opening = store.openArchive('left', '/photos.7z');
    await store.selectSource('left', 'usb');
    slow.reject({ error: { code: 'old_archive_failure', detail: 'Old archive failed.' } });
    await opening;

    expect(store.leftPanel().tabs[0]?.location).toEqual({
      kind: 'filesystem', sourceId: 'usb', path: '/',
    });
    expect(store.leftPanel().errorCode).toBe('source_unavailable');
  });

  it('repairs persisted tabs whose sources were removed', async () => {
    localStorage.setItem(
      'reachcommander.panel-state.v1',
      JSON.stringify({
        version: 1,
        activePanel: 'right',
        left: persistedPanel('removed', '/Lost'),
        right: persistedPanel('media', '/Movies'),
      }),
    );
    const api = new FakeCommanderApi([
      source('downloads', { defaultLeft: true }),
      source('media', { defaultRight: true }),
    ]);
    const store = new CommanderStore(api);

    await store.initialize();

    expect(store.leftPanel().tabs[0]?.location).toEqual({
      kind: 'filesystem', sourceId: 'downloads', path: '/',
    });
    expect(store.rightPanel().tabs[0]?.location).toEqual({
      kind: 'filesystem', sourceId: 'media', path: '/Movies',
    });
    expect(store.activePanel()).toBe('right');
  });

  it('toggles selection with Insert and advances the cursor', async () => {
    const api = new FakeCommanderApi([
      source('downloads', { defaultLeft: true, defaultRight: true }),
    ]);
    api.entries.set('downloads:/', [entry('one.txt'), entry('two.txt')]);
    const store = new CommanderStore(api);
    await store.initialize();

    store.toggleCursorSelection('left');

    expect([...store.leftPanel().selectedItems]).toEqual(['/one.txt']);
    expect(store.leftPanel().cursorIndex).toBe(1);
  });

  it('selects all visible real entries and excludes the parent row', async () => {
    const api = new FakeCommanderApi([
      source('downloads', { defaultLeft: true, defaultRight: true }),
    ]);
    api.entries.set('downloads:/Complete', [entry('one.txt'), entry('two.txt')]);
    const store = new CommanderStore(api);
    await store.initialize();
    await store.navigateTo('left', '/Complete');

    store.selectAllVisible('left');

    expect([...store.leftPanel().selectedItems].sort()).toEqual(['/one.txt', '/two.txt']);
  });

  it('supports plain, toggle, and range pointer selection', async () => {
    const api = new FakeCommanderApi([
      source('downloads', { defaultLeft: true, defaultRight: true }),
    ]);
    api.entries.set('downloads:/', [entry('alpha.txt'), entry('beta.txt'), entry('gamma.txt')]);
    const store = new CommanderStore(api);
    await store.initialize();

    store.selectWithPointer('left', 0, 'replace');
    store.selectWithPointer('left', 2, 'range');
    expect([...store.leftPanel().selectedItems].sort()).toEqual([
      '/alpha.txt',
      '/beta.txt',
      '/gamma.txt',
    ]);

    store.selectWithPointer('left', 1, 'toggle');
    expect(store.leftPanel().selectedItems.has('/beta.txt')).toBe(false);
  });

  it('clamps the cursor after filtering and keeps the opposite pane untouched', async () => {
    const api = new FakeCommanderApi([
      source('downloads', { defaultLeft: true, defaultRight: true }),
    ]);
    api.entries.set('downloads:/', [entry('alpha.txt'), entry('beta.txt')]);
    const store = new CommanderStore(api);
    await store.initialize();
    store.moveCursorBoundary('left', 'end');
    const rightBefore = store.rightPanel();

    store.setFilter('left', 'alpha');

    expect(store.leftPanel().cursorIndex).toBe(0);
    expect(store.rightPanel()).toBe(rightBefore);
  });

  it('creates rename context in visible table order rather than Set insertion order', async () => {
    const api = new FakeCommanderApi([
      source('downloads', { defaultLeft: true, defaultRight: true }),
    ]);
    api.entries.set('downloads:/', [
      entry('zeta.txt'),
      { ...entry('Drafts'), type: 'directory', extension: null, size: null },
      entry('alpha.txt'),
    ]);
    const store = new CommanderStore(api);
    await store.initialize();
    store.selectWithPointer('left', 1, 'replace');
    store.selectWithPointer('left', 0, 'toggle');

    const context = store.createMultiRenameContext('left');

    expect(context?.entries.map((item) => item.name)).toEqual(['Drafts', 'alpha.txt']);
    expect(context?.directoryPath).toBe('/');
  });

  it('uses the cursor item when there is no selection and excludes the parent row', async () => {
    const api = new FakeCommanderApi([
      source('downloads', { defaultLeft: true, defaultRight: true }),
    ]);
    api.entries.set('downloads:/Folder', [entry('one.txt')]);
    const store = new CommanderStore(api);
    await store.initialize();
    await store.navigateTo('left', '/Folder');
    store.moveCursor('left', 1);

    expect(store.createMultiRenameContext('left')?.entries.map((item) => item.name)).toEqual([
      'one.txt',
    ]);
  });

  it('opens a primary archive and navigates directories without reopening nested archives', async () => {
    const api = new FakeCommanderApi([
      source('downloads', { defaultLeft: true, defaultRight: true }),
    ]);
    api.entries.set('downloads:/', [archiveEntry('photos.7z', 'primary')]);
    api.archives.set('downloads:/photos.7z:/', archiveDirectory('/', [
      { ...entry('Family'), type: 'directory', relativePath: '/Family', extension: null, size: null },
    ]));
    api.archives.set('downloads:/photos.7z:/Family', archiveDirectory('/Family', [
      { ...entry('nested.zip'), relativePath: '/Family/nested.zip', archiveFormatHint: null, archiveRole: null },
    ]));
    const store = new CommanderStore(api);
    await store.initialize();

    await store.openEntry('left', archiveEntry('photos.7z', 'primary'));
    expect(store.leftPanel().tabs[0]?.location).toEqual({
      kind: 'archive',
      sourceId: 'downloads',
      archivePath: '/photos.7z',
      internalPath: '/',
    });
    expect(store.leftPanel().archiveMetadata).toEqual({ format: 'sevenZip', volumeCount: 2 });

    await store.openEntry('left', store.leftPanel().entries[0]!);
    expect(store.leftPanel().tabs[0]?.location).toEqual({
      kind: 'archive',
      sourceId: 'downloads',
      archivePath: '/photos.7z',
      internalPath: '/Family',
    });
    const requestsBeforeNestedOpen = api.archiveRequests.length;
    await store.openEntry('left', store.leftPanel().entries[0]!);
    expect(api.archiveRequests).toHaveLength(requestsBeforeNestedOpen);
  });

  it('returns through archive parents and crosses the archive root boundary', async () => {
    const api = new FakeCommanderApi([
      source('downloads', { defaultLeft: true, defaultRight: true }),
    ]);
    api.entries.set('downloads:/backups', []);
    api.archives.set('downloads:/backups/photos.7z:/Family', archiveDirectory('/Family'));
    api.archives.set('downloads:/backups/photos.7z:/', archiveDirectory('/'));
    const store = new CommanderStore(api);
    await store.initialize();
    await store.openArchive('left', '/backups/photos.7z');
    await store.navigateArchiveTo('left', '/Family');

    await store.navigateParent('left');
    expect(store.leftPanel().tabs[0]?.location).toEqual({
      kind: 'archive', sourceId: 'downloads', archivePath: '/backups/photos.7z', internalPath: '/',
    });

    await store.navigateParent('left');
    expect(store.leftPanel().tabs[0]?.location).toEqual({
      kind: 'filesystem', sourceId: 'downloads', path: '/backups',
    });
  });

  it('retains the filesystem location and safe API detail when a secondary part is opened', async () => {
    const api = new FakeCommanderApi([
      source('downloads', { defaultLeft: true, defaultRight: true }),
    ]);
    api.entries.set('downloads:/', [archiveEntry('photos.7z.002', 'secondary')]);
    api.archiveError = {
      error: {
        code: 'archive_volume_secondary',
        detail: 'Open the primary volume photos.7z.001.',
      },
    };
    const store = new CommanderStore(api);
    await store.initialize();

    await store.openEntry('left', api.entries.get('downloads:/')![0]!);

    expect(store.leftPanel().tabs[0]?.location).toEqual({
      kind: 'filesystem', sourceId: 'downloads', path: '/',
    });
    expect(store.leftPanel().errorCode).toBe('archive_volume_secondary');
    expect(store.leftPanel().errorDetail).toBe('Open the primary volume photos.7z.001.');
  });

  it('keeps search, sort, selection, refresh, and tabs working in archive locations', async () => {
    const api = new FakeCommanderApi([
      source('downloads', { defaultLeft: true, defaultRight: true }),
    ]);
    api.archives.set('downloads:/photos.7z:/', archiveDirectory('/', [
      entry('zeta.txt'),
      entry('alpha.jpg'),
      { ...entry('Folder'), type: 'directory', size: null, extension: null },
    ]));
    const store = new CommanderStore(api);
    await store.initialize();
    await store.openArchive('left', '/photos.7z');

    store.setFilter('left', '*.txt');
    store.sortBy('left', 'size');
    store.selectAllVisible('left');
    await store.refresh('left');
    const firstTab = store.leftPanel().activeTabId;
    await store.createTab('left');
    const secondTab = store.leftPanel().activeTabId;
    await store.activateTab('left', firstTab);
    await store.closeActiveTab('left');

    expect(store.leftPanel().filter).toBe('*.txt');
    expect(store.leftPanel().sortColumn).toBe('size');
    expect(api.archiveRequests.length).toBeGreaterThanOrEqual(5);
    expect(store.leftPanel().tabs).toHaveLength(1);
    expect(store.leftPanel().activeTabId).toBe(secondTab);
    expect(store.leftPanel().tabs[0]?.location.kind).toBe('archive');
  });

  it('retains a stale persisted archive tab and can return to its containing folder', async () => {
    localStorage.setItem(
      'reachcommander.panel-state.v1',
      JSON.stringify({
        version: 2,
        activePanel: 'left',
        left: {
          activeTabId: 'archive-tab',
          tabs: [{
            id: 'archive-tab',
            location: {
              kind: 'archive',
              sourceId: 'downloads',
              archivePath: '/backups/missing.zip',
              internalPath: '/Family',
            },
          }],
          sortColumn: 'name',
          sortDirection: 'ascending',
          filter: '',
        },
        right: {
          activeTabId: 'right-tab',
          tabs: [{
            id: 'right-tab',
            location: { kind: 'filesystem', sourceId: 'downloads', path: '/' },
          }],
          sortColumn: 'name',
          sortDirection: 'ascending',
          filter: '',
        },
      }),
    );
    const api = new FakeCommanderApi([
      source('downloads', { defaultLeft: true, defaultRight: true }),
    ]);
    api.archiveError = { error: { code: 'archive_not_found', detail: 'The archive is missing.' } };
    const store = new CommanderStore(api);

    await store.initialize();

    expect(store.leftPanel().tabs[0]?.location).toEqual({
      kind: 'archive',
      sourceId: 'downloads',
      archivePath: '/backups/missing.zip',
      internalPath: '/Family',
    });
    expect(store.leftPanel().errorCode).toBe('archive_not_found');

    api.archiveError = null;
    await store.returnArchiveToParent('left');
    expect(store.leftPanel().tabs[0]?.location).toEqual({
      kind: 'filesystem', sourceId: 'downloads', path: '/backups',
    });
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
  let reject!: (reason?: unknown) => void;
  const promise = new Promise<T>((complete, fail) => {
    resolve = complete;
    reject = fail;
  });
  return { promise, resolve, reject };
}

function source(id: string, overrides: Partial<SourceDto> = {}): SourceDto {
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
    archiveFormatHint: null,
    archiveRole: null,
  };
}

function archiveEntry(name: string, role: 'primary' | 'secondary'): FileEntryDto {
  return {
    ...entry(name),
    relativePath: `/${name}`,
    extension: name.endsWith('.rar') ? 'rar' : '7z',
    archiveFormatHint: 'sevenZip',
    archiveRole: role,
  };
}

function archiveDirectory(
  path: string,
  entries: readonly FileEntryDto[] = [],
): ArchiveDirectoryDto {
  return {
    sourceId: 'downloads',
    archivePath: '/photos.7z',
    path,
    format: 'sevenZip',
    volumeCount: 2,
    isReadOnly: true,
    entries,
  };
}

class FakeCommanderApi extends CommanderApiPort {
  readonly entries = new Map<string, readonly FileEntryDto[]>();
  readonly listRequests: { sourceId: string; path: string }[] = [];
  readonly archives = new Map<string, ArchiveDirectoryDto>();
  readonly archiveRequests: { sourceId: string; archivePath: string; internalPath: string }[] = [];
  archiveError: unknown = null;
  archiveHandler: ((
    sourceId: string,
    archivePath: string,
    internalPath: string,
  ) => Promise<ArchiveDirectoryDto>) | null = null;
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

  async listArchive(
    sourceId: string,
    archivePath: string,
    internalPath: string,
  ): Promise<ArchiveDirectoryDto> {
    this.archiveRequests.push({ sourceId, archivePath, internalPath });
    if (this.archiveHandler) {
      return this.archiveHandler(sourceId, archivePath, internalPath);
    }
    if (this.archiveError) {
      throw this.archiveError;
    }
    return this.archives.get(`${sourceId}:${archivePath}:${internalPath}`) ?? {
      ...archiveDirectory(internalPath),
      sourceId,
      archivePath,
    };
  }

  async getInfo(): Promise<FileEntryDto> {
    throw new Error('Not used by these tests');
  }

  async getUploadLimits(): Promise<UploadLimitsDto> {
    return { maxFileBytes: 10, maxBatchBytes: 20, maxFilesPerBatch: 2 };
  }

  uploadFiles(): Observable<UploadEvent> {
    return EMPTY;
  }

  async previewBatchRename(): Promise<never> {
    throw new Error('Not used by these tests');
  }

  async executeBatchRename(): Promise<never> {
    throw new Error('Not used by these tests');
  }

  async undoBatchRename(): Promise<never> {
    throw new Error('Not used by these tests');
  }

  async previewArchiveExtraction(): Promise<never> { throw new Error('Not used by these tests'); }
  async executeArchiveExtraction(): Promise<never> { throw new Error('Not used by these tests'); }
  async getArchiveExtraction(): Promise<never> { throw new Error('Not used by these tests'); }
  async cancelArchiveExtraction(): Promise<never> { throw new Error('Not used by these tests'); }
}
