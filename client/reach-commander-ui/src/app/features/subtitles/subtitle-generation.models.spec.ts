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

  it.each([
    { entries: [entry('cover.jpg')] },
    { entries: [entry('Movie.mkv', { isSymbolicLink: true })] },
    { entries: [entry('Movie.mkv', { isReadOnly: true })] },
    { entries: [entry('A.mkv'), entry('B.mkv')], selectedItems: new Set(['/A.mkv', '/B.mkv']) },
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
