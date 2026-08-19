import { PanelState } from '../../../core/state/commander.models';
import { FileEntryDto } from '../../../core/api/api.models';
import { buildVisibleRows } from './file-table.viewmodel';

describe('file table view model', () => {
  it('keeps parent first and directories ahead of files while sorting names', () => {
    const rows = buildVisibleRows(panel('/Movies', [
      entry('zeta.txt', 'file'),
      entry('Alpha', 'directory'),
      entry('beta.txt', 'file'),
      entry('Zulu', 'directory'),
    ]));

    expect(rows.map((row) => row.name)).toEqual(['..', 'Alpha', 'Zulu', 'beta.txt', 'zeta.txt']);
  });

  it('filters by name and extension case insensitively', () => {
    const state = panel('/', [
      entry('Gladiator II.mkv', 'file'),
      entry('poster.jpg', 'file'),
      entry('Movies', 'directory'),
    ], { filter: 'MKV' });

    expect(buildVisibleRows(state).map((row) => row.name)).toEqual(['Gladiator II.mkv']);
  });

  it('sorts each type group by size in descending order', () => {
    const rows = buildVisibleRows(panel('/', [
      entry('small.bin', 'file', 1),
      entry('large.bin', 'file', 10),
      entry('Folder B', 'directory'),
      entry('Folder A', 'directory'),
    ], { sortColumn: 'size', sortDirection: 'descending' }));

    expect(rows.map((row) => row.name)).toEqual(['Folder A', 'Folder B', 'large.bin', 'small.bin']);
  });
});

function panel(
  path: string,
  entries: readonly FileEntryDto[],
  overrides: Partial<PanelState> = {},
): PanelState {
  return {
    sourceId: 'media',
    tabs: [{ id: 'tab', label: 'Movies', sourceId: 'media', path }],
    activeTabId: 'tab',
    cursorIndex: 0,
    selectedItems: new Set<string>(),
    selectionAnchor: null,
    sortColumn: 'name',
    sortDirection: 'ascending',
    filter: '',
    entries,
    loading: false,
    errorCode: null,
    requestToken: 1,
    ...overrides,
  };
}

function entry(name: string, type: 'file' | 'directory', size: number | null = null): FileEntryDto {
  return {
    name,
    relativePath: `/Movies/${name}`,
    type,
    size,
    modifiedAt: '2026-08-19T10:00:00Z',
    extension: type === 'file' ? name.split('.').at(-1) ?? null : null,
    isReadOnly: false,
    isSymbolicLink: false,
    attributes: 'Normal',
  };
}
