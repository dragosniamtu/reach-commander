import { PanelPersistence } from './panel-persistence';
import { ArchiveLocation, PanelState } from './commander.models';

describe('PanelPersistence', () => {
  beforeEach(() => localStorage.clear());

  it('round trips v2 filesystem and archive locations without transient archive metadata', () => {
    const persistence = new PanelPersistence(localStorage);
    const archiveLocation: ArchiveLocation = {
      kind: 'archive',
      sourceId: 'media',
      archivePath: '/backups/photos.7z',
      internalPath: '/Family/2025',
    };
    persistence.save(
      panel({ kind: 'filesystem', sourceId: 'downloads', path: '/Complete' }),
      panel(archiveLocation),
      'right',
    );

    const restored = persistence.load();

    expect(restored?.activePanel).toBe('right');
    expect(restored?.version).toBe(2);
    expect(restored?.left.tabs[0]?.location).toEqual({
      kind: 'filesystem',
      sourceId: 'downloads',
      path: '/Complete',
    });
    expect(restored?.right.tabs[0]?.location).toEqual(archiveLocation);
    expect(JSON.stringify(restored)).not.toContain('selectedItems');
    expect(JSON.stringify(restored)).not.toContain('entries');
    expect(JSON.stringify(restored)).not.toContain('archiveMetadata');
  });

  it('migrates valid version-1 filesystem tabs to v2 locations', () => {
    const persistence = new PanelPersistence(localStorage);
    localStorage.setItem(
      PanelPersistence.storageKey,
      JSON.stringify({
        version: 1,
        activePanel: 'left',
        left: persistedV1Panel('downloads', '/Complete'),
        right: persistedV1Panel('media', '/Movies'),
      }),
    );

    const restored = persistence.load();

    expect(restored?.version).toBe(2);
    expect(restored?.left.tabs[0]?.location).toEqual({
      kind: 'filesystem',
      sourceId: 'downloads',
      path: '/Complete',
    });
  });

  it('rejects invalid JSON and unsupported versions', () => {
    const persistence = new PanelPersistence(localStorage);
    localStorage.setItem(PanelPersistence.storageKey, '{broken');
    expect(persistence.load()).toBeNull();

    localStorage.setItem(PanelPersistence.storageKey, JSON.stringify({ version: 99 }));
    expect(persistence.load()).toBeNull();

    localStorage.setItem(
      PanelPersistence.storageKey,
      JSON.stringify({
        version: 2,
        activePanel: 'left',
        left: {
          activeTabId: 'bad',
          tabs: [{ id: 'bad', location: { kind: 'archive', sourceId: 'media', archivePath: '/x.zip' } }],
          sortColumn: 'name',
          sortDirection: 'ascending',
          filter: '',
        },
        right: persistedV2Panel({ kind: 'filesystem', sourceId: 'media', path: '/' }),
      }),
    );
    expect(persistence.load()).toBeNull();
  });

  it('rejects persisted locations that mix filesystem and archive fields', () => {
    const persistence = new PanelPersistence(localStorage);
    for (const location of [
      {
        kind: 'filesystem',
        sourceId: 'downloads',
        path: '/',
        archivePath: '/photos.zip',
        internalPath: '/',
      },
      {
        kind: 'archive',
        sourceId: 'downloads',
        archivePath: '/photos.zip',
        internalPath: '/',
        path: '/',
      },
    ]) {
      localStorage.setItem(
        PanelPersistence.storageKey,
        JSON.stringify({
          version: 2,
          activePanel: 'left',
          left: persistedV2Panel(location),
          right: persistedV2Panel({ kind: 'filesystem', sourceId: 'media', path: '/' }),
        }),
      );
      expect(persistence.load()).toBeNull();
    }
  });

  it('clears the persisted workspace when the authenticated user locks', () => {
    const persistence = new PanelPersistence(localStorage);
    localStorage.setItem(PanelPersistence.storageKey, '{"sensitive":"workspace"}');

    persistence.clear();

    expect(localStorage.getItem(PanelPersistence.storageKey)).toBeNull();
  });
});

function persistedV1Panel(sourceId: string, path: string) {
  return {
    activeTabId: `${sourceId}-tab`,
    tabs: [{ id: `${sourceId}-tab`, label: sourceId, sourceId, path }],
    sortColumn: 'name',
    sortDirection: 'ascending',
    filter: '',
  };
}

function persistedV2Panel(location: object) {
  return {
    activeTabId: 'tab',
    tabs: [{ id: 'tab', location }],
    sortColumn: 'name',
    sortDirection: 'ascending',
    filter: '',
  };
}

function panel(location: PanelState['tabs'][number]['location']): PanelState {
  return {
    tabs: [{ id: `${location.sourceId}-tab`, label: location.sourceId, location }],
    activeTabId: `${location.sourceId}-tab`,
    cursorIndex: 4,
    selectedItems: new Set(['/selected']),
    selectionAnchor: 4,
    sortColumn: 'modifiedAt',
    sortDirection: 'descending',
    filter: 'movie',
    entries: [{
      name: 'selected',
      relativePath: '/selected',
      type: 'file',
      size: 1,
      modifiedAt: '2026-08-19T10:00:00Z',
      extension: null,
      isReadOnly: false,
      isSymbolicLink: false,
      attributes: 'Normal',
      archiveFormatHint: null,
      archiveRole: null,
    }],
    loading: false,
    errorCode: null,
    errorDetail: null,
    archiveMetadata: location.kind === 'archive' ? { format: 'sevenZip', volumeCount: 1 } : null,
    requestToken: 8,
  };
}
