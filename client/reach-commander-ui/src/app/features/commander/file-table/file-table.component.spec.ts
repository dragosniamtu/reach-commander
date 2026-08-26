import { ComponentFixture, TestBed } from '@angular/core/testing';
import { PanelState } from '../../../core/state/commander.models';
import { FileTableComponent } from './file-table.component';

describe('FileTableComponent', () => {
  let fixture: ComponentFixture<FileTableComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [FileTableComponent] }).compileComponents();
    fixture = TestBed.createComponent(FileTableComponent);
    fixture.componentRef.setInput('panel', panel());
    fixture.detectChanges();
  });

  it('renders dense sortable headers and selected row state', () => {
    const nameHeader = fixture.nativeElement.querySelector('[data-sort="name"]');
    const selected = fixture.nativeElement.querySelector('tbody tr[aria-selected="true"]');

    expect(nameHeader.closest('th')?.getAttribute('aria-sort')).toBe('ascending');
    expect(selected.textContent).toContain('movie.mkv');

    const name = selected.querySelector('.file-name') as HTMLElement;
    const nameContent = selected.querySelector('.name-content');

    expect(nameContent).not.toBeNull();
    expect(name.textContent?.trim()).toBe('movie.mkv');
    expect(name.title).toBe('movie.mkv');
  });

  it('emits pointer selection with modifier intent', () => {
    const selected = vi.fn();
    fixture.componentInstance.rowSelected.subscribe(selected);
    const row = fixture.nativeElement.querySelector('tbody tr');

    row.dispatchEvent(new MouseEvent('click', { bubbles: true, ctrlKey: true }));

    expect(selected).toHaveBeenCalledWith({ rowIndex: 0, mode: 'toggle' });
  });

  it('distinguishes openable archives, secondary volumes, and nested archive files accessibly', () => {
    fixture.componentRef.setInput('panel', panel({
      entries: [
        { ...file('photos.7z'), archiveFormatHint: 'sevenZip', archiveRole: 'primary' },
        { ...file('photos.7z.002'), archiveFormatHint: 'sevenZip', archiveRole: 'secondary' },
      ],
      selectedItems: new Set<string>(),
    }));
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('.type-icon.archive')).toBeTruthy();
    expect(fixture.nativeElement.querySelector('.type-icon.volume')).toBeTruthy();
    expect(fixture.nativeElement.textContent).toContain('Open the primary volume instead');

    const volumeName = fixture.nativeElement.querySelector(
      'tr[data-path="/photos.7z.002"] .file-name',
    ) as HTMLElement;

    expect(volumeName.title).toBe(
      'photos.7z.002\nArchive volume part. Open the primary volume instead.',
    );

    fixture.componentRef.setInput('panel', panel({
      tabs: [{
        id: 'tab',
        label: 'photos.7z',
        location: {
          kind: 'archive',
          sourceId: 'media',
          archivePath: '/photos.7z',
          internalPath: '/',
        },
      }],
      entries: [file('nested.zip')],
      archiveMetadata: { format: 'sevenZip', volumeCount: 1 },
    }));
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('Nested archive browsing is unavailable');
    expect(fixture.nativeElement.querySelector('.type-icon.archive')).toBeNull();
  });
});

function panel(overrides: Partial<PanelState> = {}): PanelState {
  return {
    tabs: [{
      id: 'tab',
      label: 'Movies',
      location: { kind: 'filesystem', sourceId: 'media', path: '/' },
    }],
    activeTabId: 'tab',
    cursorIndex: 0,
    selectedItems: new Set(['/movie.mkv']),
    selectionAnchor: 0,
    sortColumn: 'name',
    sortDirection: 'ascending',
    filter: '',
    entries: [file('movie.mkv')],
    loading: false,
    errorCode: null,
    errorDetail: null,
    archiveMetadata: null,
    requestToken: 1,
    ...overrides,
  };
}

function file(name: string) {
  return {
    name,
    relativePath: `/${name}`,
    type: 'file',
    size: 1024,
    modifiedAt: '2026-08-19T10:00:00Z',
    extension: name.split('.').at(-1) ?? null,
    isReadOnly: false,
    isSymbolicLink: false,
    attributes: 'Normal',
    archiveFormatHint: null,
    archiveRole: null,
  } as const;
}
