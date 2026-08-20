import { ComponentFixture, TestBed } from '@angular/core/testing';
import { SourceDto } from '../../../core/api/api.models';
import { CommanderStore } from '../../../core/state/commander-store';
import { PanelState } from '../../../core/state/commander.models';
import { CommanderPanelComponent } from './commander-panel.component';

describe('CommanderPanelComponent archive locations', () => {
  let fixture: ComponentFixture<CommanderPanelComponent>;
  const store = {
    activatePanel: vi.fn(),
    selectSource: vi.fn(),
    activateTab: vi.fn(),
    closeActiveTab: vi.fn(),
    createTab: vi.fn(),
    setPathFromEditor: vi.fn(),
    sortBy: vi.fn(),
    selectWithPointer: vi.fn(),
    openEntry: vi.fn(),
    refresh: vi.fn(),
    returnArchiveToParent: vi.fn(),
  };

  beforeEach(async () => {
    vi.clearAllMocks();
    await TestBed.configureTestingModule({
      imports: [CommanderPanelComponent],
      providers: [{ provide: CommanderStore, useValue: store }],
    }).compileComponents();
    fixture = TestBed.createComponent(CommanderPanelComponent);
    fixture.componentRef.setInput('side', 'left');
    fixture.componentRef.setInput('sources', [source()]);
    fixture.componentRef.setInput('active', true);
  });

  it('shows the exact read-only archive path, routes double-click, and restores panel focus', async () => {
    const state = archivePanel();
    fixture.componentRef.setInput('panel', state);
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('Archive · RO');
    expect(fixture.nativeElement.textContent).toContain(
      'Downloads:/backups/photos.7z!/Family/2025',
    );
    expect(fixture.nativeElement.querySelector('.path-display').getAttribute('aria-readonly'))
      .toBe('true');

    const row = fixture.nativeElement.querySelector('tbody tr:last-child') as HTMLTableRowElement;
    row.dispatchEvent(new MouseEvent('dblclick', { bubbles: true }));
    expect(store.openEntry).toHaveBeenCalledWith('left', expect.objectContaining({ name: 'photo.jpg' }));
    await fixture.whenStable();
    expect(document.activeElement).toBe(fixture.nativeElement.querySelector('.panel'));
  });

  it('retains a failed archive tab and exposes a return-to-parent action', () => {
    fixture.componentRef.setInput('panel', archivePanel({
      entries: [],
      errorCode: 'archive_not_found',
      errorDetail: 'The archive is no longer available.',
      archiveMetadata: null,
    }));
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('The archive is no longer available.');
    expect(fixture.nativeElement.querySelector('.error-state').getAttribute('role')).toBeNull();
    expect(fixture.nativeElement.querySelector('.panel-announcer').getAttribute('aria-live'))
      .toBe('polite');
    const button = [...fixture.nativeElement.querySelectorAll('button')].find(
      (candidate: HTMLButtonElement) => candidate.textContent?.includes('Return to parent folder'),
    ) as HTMLButtonElement;
    button.click();
    expect(store.returnArchiveToParent).toHaveBeenCalledWith('left');
  });

  it('keeps filesystem path editing scoped to the logical path without the source label', () => {
    fixture.componentRef.setInput('panel', archivePanel({
      tabs: [{
        id: 'filesystem-tab',
        label: 'Complete',
        location: { kind: 'filesystem', sourceId: 'downloads', path: '/Complete' },
      }],
      activeTabId: 'filesystem-tab',
      archiveMetadata: null,
    }));
    fixture.detectChanges();

    fixture.componentInstance.focusPath();
    fixture.detectChanges();
    expect((fixture.nativeElement.querySelector('.path-input') as HTMLInputElement).value)
      .toBe('/Complete');
  });

  it('announces an empty location through the stable polite live region', () => {
    fixture.componentRef.setInput('panel', archivePanel({ entries: [] }));
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('.empty-state').textContent).toContain('empty');
    expect(fixture.nativeElement.querySelector('.panel-announcer').textContent).toContain('items');
  });

  it('announces secondary-volume guidance for the keyboard cursor', () => {
    fixture.componentRef.setInput('panel', archivePanel({
      tabs: [{
        id: 'filesystem-tab',
        label: '/',
        location: { kind: 'filesystem', sourceId: 'downloads', path: '/' },
      }],
      activeTabId: 'filesystem-tab',
      cursorIndex: 0,
      entries: [{
        ...archivePanel().entries[0]!,
        name: 'photos.7z.002',
        relativePath: '/photos.7z.002',
        archiveFormatHint: 'sevenZip',
        archiveRole: 'secondary',
      }],
      archiveMetadata: null,
    }));
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('.panel-announcer').textContent)
      .toContain('Open the primary volume instead');
  });
});

function archivePanel(overrides: Partial<PanelState> = {}): PanelState {
  return {
    tabs: [{
      id: 'archive-tab',
      label: '2025',
      location: {
        kind: 'archive',
        sourceId: 'downloads',
        archivePath: '/backups/photos.7z',
        internalPath: '/Family/2025',
      },
    }],
    activeTabId: 'archive-tab',
    cursorIndex: 1,
    selectedItems: new Set<string>(),
    selectionAnchor: null,
    sortColumn: 'name',
    sortDirection: 'ascending',
    filter: '',
    entries: [{
      name: 'photo.jpg',
      relativePath: '/Family/2025/photo.jpg',
      type: 'file',
      size: 12,
      modifiedAt: null,
      extension: 'jpg',
      isReadOnly: true,
      isSymbolicLink: false,
      attributes: 'Archive',
      archiveFormatHint: null,
      archiveRole: null,
    }],
    loading: false,
    errorCode: null,
    errorDetail: null,
    archiveMetadata: { format: 'sevenZip', volumeCount: 2 },
    requestToken: 1,
    ...overrides,
  };
}

function source(): SourceDto {
  return {
    id: 'downloads',
    name: 'Downloads',
    isAvailable: true,
    isReadOnly: false,
    totalBytes: 100,
    usedBytes: 20,
    freeBytes: 80,
    defaultLeft: true,
    defaultRight: true,
  };
}
