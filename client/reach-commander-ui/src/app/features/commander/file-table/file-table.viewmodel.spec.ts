import { PanelState } from '../../../core/state/commander.models';
import { FileEntryDto } from '../../../core/api/api.models';
import { buildVisibleRows } from './file-table.viewmodel';

describe('file table view model', () => {
  it('keeps parent first and directories ahead of files while sorting names', () => {
    const rows = buildVisibleRows(
      panel('/Movies', [
        entry('zeta.txt', 'file'),
        entry('Alpha', 'directory'),
        entry('beta.txt', 'file'),
        entry('Zulu', 'directory'),
      ]),
    );

    expect(rows.map((row) => row.name)).toEqual(['..', 'Alpha', 'Zulu', 'beta.txt', 'zeta.txt']);
  });

  it('filters by name and extension case insensitively', () => {
    const state = panel(
      '/',
      [
        entry('Gladiator II.mkv', 'file'),
        entry('poster.jpg', 'file'),
        entry('Movies', 'directory'),
      ],
      { filter: 'MKV' },
    );

    expect(buildVisibleRows(state).map((row) => row.name)).toEqual(['Gladiator II.mkv']);
  });

  it('sorts each type group by size in descending order', () => {
    const rows = buildVisibleRows(
      panel(
        '/',
        [
          entry('small.bin', 'file', 1),
          entry('large.bin', 'file', 10),
          entry('Folder B', 'directory'),
          entry('Folder A', 'directory'),
        ],
        { sortColumn: 'size', sortDirection: 'descending' },
      ),
    );

    expect(rows.map((row) => row.name)).toEqual(['Folder A', 'Folder B', 'large.bin', 'small.bin']);
  });

  it('matches anchored star and question-mark wildcard patterns by complete name', () => {
    const entries = [
      entry('tool.exe', 'file'),
      entry('tool.exe.backup', 'file'),
      entry('runner.exe', 'directory'),
      entry('report-01.pdf', 'file'),
      entry('report-1.pdf', 'file'),
    ];

    expect(
      buildVisibleRows(panel('/', entries, { filter: '*.exe' })).map((row) => row.name),
    ).toEqual(['runner.exe', 'tool.exe']);
    expect(
      buildVisibleRows(panel('/', entries, { filter: 'report-??.pdf' })).map((row) => row.name),
    ).toEqual(['report-01.pdf']);
  });

  it('keeps substring behavior without wildcards and always preserves the parent row', () => {
    const entries = [entry('alpha-notes.txt', 'file'), entry('beta.exe', 'file')];

    expect(
      buildVisibleRows(panel('/Folder', entries, { filter: 'notes' })).map((row) => row.name),
    ).toEqual(['..', 'alpha-notes.txt']);
    expect(
      buildVisibleRows(panel('/Folder', entries, { filter: '*.zip' })).map((row) => row.name),
    ).toEqual(['..']);
  });

  it('sorts missing archive modified dates deterministically by name', () => {
    const rows = buildVisibleRows(panel('/', [
      { ...entry('zeta.txt', 'file'), modifiedAt: null },
      { ...entry('alpha.txt', 'file'), modifiedAt: null },
      { ...entry('middle.txt', 'file'), modifiedAt: '2026-08-19T10:00:00Z' },
    ], { sortColumn: 'modifiedAt' }));

    expect(rows.map((row) => row.name)).toEqual(['middle.txt', 'alpha.txt', 'zeta.txt']);
  });
});

function panel(
  path: string,
  entries: readonly FileEntryDto[],
  overrides: Partial<PanelState> = {},
): PanelState {
  return {
    tabs: [{
      id: 'tab',
      label: 'Movies',
      location: { kind: 'filesystem', sourceId: 'media', path },
    }],
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
    errorDetail: null,
    archiveMetadata: null,
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
    extension: type === 'file' ? (name.split('.').at(-1) ?? null) : null,
    isReadOnly: false,
    isSymbolicLink: false,
    attributes: 'Normal',
    archiveFormatHint: null,
    archiveRole: null,
  };
}
