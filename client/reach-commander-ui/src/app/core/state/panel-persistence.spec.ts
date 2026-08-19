import { PanelPersistence } from './panel-persistence';
import { PanelState } from './commander.models';

describe('PanelPersistence', () => {
  beforeEach(() => localStorage.clear());

  it('round trips durable panel state without selection or entries', () => {
    const persistence = new PanelPersistence(localStorage);
    persistence.save(panel('downloads', '/Complete'), panel('media', '/Movies'), 'right');

    const restored = persistence.load();

    expect(restored?.activePanel).toBe('right');
    expect(restored?.left.tabs[0]?.path).toBe('/Complete');
    expect(JSON.stringify(restored)).not.toContain('selectedItems');
    expect(JSON.stringify(restored)).not.toContain('entries');
  });

  it('rejects invalid JSON and unsupported versions', () => {
    const persistence = new PanelPersistence(localStorage);
    localStorage.setItem(PanelPersistence.storageKey, '{broken');
    expect(persistence.load()).toBeNull();

    localStorage.setItem(PanelPersistence.storageKey, JSON.stringify({ version: 99 }));
    expect(persistence.load()).toBeNull();
  });
});

function panel(sourceId: string, path: string): PanelState {
  return {
    sourceId,
    tabs: [{ id: `${sourceId}-tab`, label: sourceId, sourceId, path }],
    activeTabId: `${sourceId}-tab`,
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
    }],
    loading: false,
    errorCode: null,
    requestToken: 8,
  };
}
