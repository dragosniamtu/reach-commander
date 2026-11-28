import { FileEntryDto, SourceDto } from '../../core/api/api.models';
import { PanelState } from '../../core/state/commander.models';
import { captureSubtitleContext } from './subtitle-generation.models';

describe('subtitle selection', () => {
  const source: SourceDto = {
    id: 'media',
    name: 'Media',
    isAvailable: true,
    isReadOnly: false,
    totalBytes: null,
    usedBytes: null,
    freeBytes: null,
    defaultLeft: true,
    defaultRight: true,
  };
  function entry(name = 'Movie.mkv', overrides: Partial<FileEntryDto> = {}): FileEntryDto {
    return {
      name,
      relativePath: '/' + name,
      type: 'file',
      size: 20,
      modifiedAt: null,
      extension: 'mkv',
      isReadOnly: false,
      isSymbolicLink: false,
      attributes: '',
      archiveFormatHint: null,
      archiveRole: null,
      ...overrides,
    };
  }
  function panel(overrides: Partial<PanelState> = {}): PanelState {
    return {
      tabs: [
        {
          id: 'tab',
          label: 'Media',
          location: { kind: 'filesystem', sourceId: 'media', path: '/' },
        },
      ],
      activeTabId: 'tab',
      cursorIndex: 0,
      selectedItems: new Set(),
      selectionAnchor: null,
      sortColumn: 'name',
      sortDirection: 'ascending',
      filter: '',
      entries: [entry()],
      loading: false,
      errorCode: null,
      errorDetail: null,
      archiveMetadata: null,
      requestToken: 0,
      ...overrides,
    };
  }

  it.each(['mkv', 'mp4', 'avi', 'mov', 'm4v', 'webm', 'MKV'])(
    'captures one focused %s video',
    (extension) => {
      expect(
        captureSubtitleContext('left', panel({ entries: [entry('Movie.' + extension)] }), [source])
          .context,
      ).toEqual({
        panelSide: 'left',
        sourceId: 'media',
        sourceName: 'Media',
        path: '/Movie.' + extension,
      });
    },
  );

  it('captures a selected folder using its logical path', () => {
    const selected = panel({
      entries: [entry('Season 1', { type: 'directory' })],
      selectedItems: new Set(['/Season 1']),
    });
    expect(captureSubtitleContext('right', selected, [source]).context?.path).toBe('/Season 1');
  });

  it('captures all selected videos in visible order without including unselected files', () => {
    const selected = panel({
      entries: [entry('C.mp4'), entry('A.mkv'), entry('B.webm')],
      selectedItems: new Set(['/C.mp4', '/A.mkv']),
    });
    expect(captureSubtitleContext('left', selected, [source])).toEqual({
      context: {
        panelSide: 'left',
        sourceId: 'media',
        sourceName: 'Media',
        path: '/A.mkv',
        paths: ['/A.mkv', '/C.mp4'],
      },
      error: null,
    });
  });

  it('uses the displayed descending order for a batch', () => {
    const selected = panel({
      entries: [entry('A.mkv'), entry('B.mp4')],
      selectedItems: new Set(['/A.mkv', '/B.mp4']),
      sortDirection: 'descending',
    });
    expect(captureSubtitleContext('right', selected, [source]).context).toMatchObject({
      path: '/B.mp4',
      paths: ['/B.mp4', '/A.mkv'],
    });
  });

  it('explains how to restore a wholly filtered selection', () => {
    const selected = panel({ selectedItems: new Set(['/Movie.mkv']), filter: '*.mp4' });
    expect(captureSubtitleContext('left', selected, [source]).error).toContain(
      'Select the visible videos again',
    );
  });

  it('rejects Linux filenames that would be normalized into a different video path', () => {
    const selected = panel({
      entries: [entry('Movie.mkv'), entry('Season\\Pilot.mkv')],
      selectedItems: new Set(['/Movie.mkv', '/Season\\Pilot.mkv']),
    });
    expect(captureSubtitleContext('left', selected, [source]).context).toBeNull();
    expect(captureSubtitleContext('left', selected, [source]).error).toContain('backslash');
  });

  it.each([
    { entries: [entry('cover.jpg')] },
    { entries: [entry('Movie.mkv', { isSymbolicLink: true })] },
    { entries: [entry('Movie.mkv', { isReadOnly: true })] },
    {
      entries: [entry('A.mkv'), entry('notes.txt')],
      selectedItems: new Set(['/A.mkv', '/notes.txt']),
    },
    {
      entries: [entry('A.mkv'), entry('B.mkv', { isSymbolicLink: true })],
      selectedItems: new Set(['/A.mkv', '/B.mkv']),
    },
    {
      entries: [entry('A.mkv'), entry('B.mkv', { isReadOnly: true })],
      selectedItems: new Set(['/A.mkv', '/B.mkv']),
    },
    {
      entries: [entry('A.mkv'), entry('TV', { type: 'directory' })],
      selectedItems: new Set(['/A.mkv', '/TV']),
    },
    {
      entries: [entry('A.mkv'), entry('B.mkv')],
      selectedItems: new Set(['/A.mkv', '/B.mkv']),
      filter: 'A',
    },
    { selectedItems: new Set(['/Hidden.mkv', '/Movie.mkv']) },
    { loading: true },
    { errorCode: 'source_unavailable' },
    {
      tabs: [
        {
          id: 'tab',
          label: 'Archive',
          location: {
            kind: 'archive' as const,
            sourceId: 'media',
            archivePath: '/a.zip',
            internalPath: '/',
          },
        },
      ],
    },
  ])('rejects ineligible panel %j', (changes) => {
    expect(captureSubtitleContext('left', panel(changes), [source]).context).toBeNull();
  });

  it('rejects read-only and unavailable sources and the parent row', () => {
    expect(
      captureSubtitleContext('left', panel(), [{ ...source, isReadOnly: true }]).context,
    ).toBeNull();
    expect(
      captureSubtitleContext('left', panel(), [{ ...source, isAvailable: false }]).context,
    ).toBeNull();
    const parent = panel({
      tabs: [
        {
          id: 'tab',
          label: 'TV',
          location: { kind: 'filesystem', sourceId: 'media', path: '/TV' },
        },
      ],
    });
    expect(captureSubtitleContext('left', parent, [source]).context).toBeNull();
  });
});
