import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Subject } from 'rxjs';
import { CommanderKeyboardService } from '../../../core/keyboard/commander-keyboard.service';
import { SourceDto } from '../../../core/api/api.models';
import { CommanderStore } from '../../../core/state/commander-store';
import { PanelState } from '../../../core/state/commander.models';
import { SystemMetricsStore } from '../../../core/state/system-metrics-store';
import { UploadStore } from '../../../core/state/upload-store';
import { UploadState } from '../../../core/state/upload.models';
import { MultiRenameStore } from '../../../core/state/multi-rename-store';
import { MultiRenameState } from '../../../core/state/multi-rename.models';
import { CommanderShellComponent } from './commander-shell.component';

describe('CommanderShellComponent system metrics integration', () => {
  let fixture: ComponentFixture<CommanderShellComponent>;
  const keyboard = {
    commands: new Subject<any>(),
    start: vi.fn(),
    stop: vi.fn(),
  };
  const metrics = {
    start: vi.fn(),
    stop: vi.fn(),
    state: signal({
      snapshot: null,
      pending: false,
      errorCode: null,
      requestToken: 0,
      nowEpochMilliseconds: Date.now(),
    }),
    effectiveSnapshot: signal(null),
    effectiveState: signal<'loading'>('loading'),
  };
  const store = {
    sources: signal<readonly SourceDto[]>([]),
    leftPanel: signal<PanelState>(panel()),
    rightPanel: signal<PanelState>(panel()),
    activePanel: signal<'left' | 'right'>('left'),
    initialize: vi.fn(() => Promise.resolve()),
    refresh: vi.fn(() => Promise.resolve()),
    clearSelection: vi.fn(),
    createMultiRenameContext: vi.fn(() => null),
    activatePanel: vi.fn(),
    setFilter: vi.fn(),
    openEntry: vi.fn(() => Promise.resolve()),
  };
  const upload = {
    state: signal<UploadState>(closedUploadState()),
    isPending: vi.fn(() => false),
    open: vi.fn(),
    start: vi.fn(() => true),
    cancel: vi.fn(() => true),
    close: vi.fn(() => true),
  };
  const multiRename = {
    state: signal<MultiRenameState>(closedMultiRenameState()),
    canExecute: signal(false),
    canUndo: signal(false),
    open: vi.fn(),
    close: vi.fn(),
    updateRules: vi.fn(),
    execute: vi.fn(() => Promise.resolve(false)),
    undo: vi.fn(() => Promise.resolve(false)),
  };

  beforeEach(async () => {
    vi.clearAllMocks();
    store.sources.set([]);
    store.leftPanel.set(panel());
    store.rightPanel.set(panel());
    store.activePanel.set('left');
    upload.state.set(closedUploadState());
    multiRename.state.set(closedMultiRenameState());
    await TestBed.configureTestingModule({
      imports: [CommanderShellComponent],
      providers: [
        { provide: CommanderKeyboardService, useValue: keyboard },
        { provide: CommanderStore, useValue: store },
        { provide: SystemMetricsStore, useValue: metrics },
        { provide: UploadStore, useValue: upload },
        { provide: MultiRenameStore, useValue: multiRename },
      ],
    }).compileComponents();
    fixture = TestBed.createComponent(CommanderShellComponent);
    fixture.detectChanges();
  });

  it('places the widget last, opens details, and starts only one polling lifecycle', () => {
    const actions = fixture.nativeElement.querySelector('.top-actions');
    expect(actions.lastElementChild?.tagName).toBe('APP-SYSTEM-METRICS-WIDGET');
    expect(metrics.start).toHaveBeenCalledOnce();

    (
      fixture.nativeElement.querySelector(
        '[data-testid="system-metrics-trigger"]',
      ) as HTMLButtonElement
    ).click();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[role="dialog"]')).not.toBeNull();
    expect(metrics.start).toHaveBeenCalledOnce();
  });

  it('stops polling when the shell is destroyed', () => {
    fixture.destroy();
    expect(metrics.stop).toHaveBeenCalledOnce();
  });

  it('handles Escape by closing metrics before commander state changes', () => {
    fixture.componentInstance.openMetrics();

    fixture.componentInstance.execute({ type: 'escape' });

    expect(fixture.componentInstance.metricsOpen()).toBe(false);
    expect(metrics.start).toHaveBeenCalledOnce();
  });

  it('captures the active panel destination and refreshes only that panel after completion', () => {
    store.sources.set([source('downloads', 'Downloads'), source('media', 'Media')]);
    store.leftPanel.set(
      panel({
        tabs: [{
          id: 'left-tab',
          label: 'Complete',
          location: { kind: 'filesystem', sourceId: 'downloads', path: '/Complete' },
        }],
        activeTabId: 'left-tab',
        filter: '*.zip',
        selectedItems: new Set(['/existing.zip']),
      }),
    );
    store.rightPanel.set(
      panel({
        tabs: [{
          id: 'right-tab',
          label: 'Movies',
          location: { kind: 'filesystem', sourceId: 'media', path: '/Movies' },
        }],
        activeTabId: 'right-tab',
      }),
    );
    const files = [new File(['one'], 'one.txt')];

    fixture.componentInstance.reviewUpload(files);
    const [context, capturedFiles, onCompleted] = upload.open.mock.calls[0]!;
    store.activePanel.set('right');
    onCompleted();

    expect(context).toEqual({
      side: 'left',
      sourceId: 'downloads',
      sourceName: 'Downloads',
      directoryPath: '/Complete',
    });
    expect(capturedFiles).toEqual(files);
    expect(store.refresh).toHaveBeenCalledOnce();
    expect(store.refresh).toHaveBeenCalledWith('left');
    expect(store.leftPanel().filter).toBe('*.zip');
    expect([...store.leftPanel().selectedItems]).toEqual(['/existing.zip']);
  });

  it('rejects unavailable or read-only upload destinations before opening a review', () => {
    store.sources.set([source('archive', 'Archive', { isReadOnly: true })]);
    store.leftPanel.set(
      panel({
        tabs: [{
          id: 'tab',
          label: '/',
          location: { kind: 'filesystem', sourceId: 'archive', path: '/' },
        }],
      }),
    );

    fixture.componentInstance.reviewUpload([new File(['one'], 'one.txt')]);

    expect(upload.open).not.toHaveBeenCalled();
    expect(fixture.componentInstance.commandStatus()).toBe('Archive is read-only.');
  });

  it('delegates start and close while giving metrics first Escape priority', () => {
    upload.state.set({ ...closedUploadState(), phase: 'review' });
    fixture.componentInstance.openMetrics();

    fixture.componentInstance.execute({ type: 'escape' });
    expect(upload.close).not.toHaveBeenCalled();

    fixture.componentInstance.execute({ type: 'escape' });
    expect(upload.close).toHaveBeenCalledOnce();

    fixture.componentInstance.startUpload();
    fixture.componentInstance.closeUpload();
    expect(upload.start).toHaveBeenCalledOnce();
    expect(upload.close).toHaveBeenCalledTimes(2);
  });

  it('refreshes only the originating pane and restores its focus after rename', async () => {
    const rightPanelBefore = store.rightPanel();
    multiRename.state.set({
      ...closedMultiRenameState(),
      open: true,
      context: {
        panelSide: 'left',
        sourceId: 'downloads',
        sourceName: 'Downloads',
        directoryPath: '/',
        entries: [],
        isAvailable: true,
        isReadOnly: false,
      },
    });
    fixture.detectChanges();
    const leftPanel = fixture.componentInstance['leftPanel']!;
    const focus = vi.spyOn(leftPanel, 'focusPanel');

    await fixture.componentInstance.handleRenameFilesystemChanged('left');

    expect(store.clearSelection).toHaveBeenCalledWith('left');
    expect(store.refresh).toHaveBeenCalledWith('left');
    expect(store.rightPanel()).toBe(rightPanelBefore);
    fixture.componentInstance.closeMultiRename();
    await Promise.resolve();
    expect(focus).toHaveBeenCalledOnce();
  });

  it('renders active panel context and routes toolbar search to that side', () => {
    store.sources.set([source('downloads', 'Downloads'), source('media', 'Media')]);
    store.leftPanel.set(
      panel({
        tabs: [{
          id: 'left',
          label: '/',
          location: { kind: 'filesystem', sourceId: 'downloads', path: '/incoming' },
        }],
        activeTabId: 'left',
        filter: '*.txt',
      }),
    );
    store.rightPanel.set(
      panel({
        tabs: [{
          id: 'right',
          label: '/',
          location: { kind: 'filesystem', sourceId: 'media', path: '/Movies' },
        }],
        activeTabId: 'right',
        filter: '*.mkv',
      }),
    );
    fixture.detectChanges();

    expect(
      fixture.nativeElement.querySelector('[data-testid="active-panel-context"]').textContent,
    ).toContain('LEFT · Downloads');
    const search = fixture.nativeElement.querySelector(
      '[aria-label="Search active panel"]',
    ) as HTMLInputElement;
    expect(search.value).toBe('*.txt');
    search.value = '*.zip';
    search.dispatchEvent(new Event('input'));
    expect(store.setFilter).toHaveBeenCalledWith('left', '*.zip');

    store.activePanel.set('right');
    fixture.detectChanges();
    expect(
      (
        fixture.nativeElement.querySelector(
          '[aria-label="Search active panel"]',
        ) as HTMLInputElement
      ).value,
    ).toBe('*.mkv');
  });

  it('places toolbar between brand and metrics actions and focuses search with Ctrl+F', async () => {
    const topbar = fixture.nativeElement.querySelector('.topbar') as HTMLElement;

    expect(topbar.children[0]?.classList.contains('brand-block')).toBe(true);
    expect(topbar.children[1]?.tagName).toBe('APP-ACTIVE-PANEL-TOOLBAR');
    expect(topbar.children[2]?.classList.contains('top-actions')).toBe(true);

    fixture.componentInstance.execute({ type: 'focus-search' });
    await Promise.resolve();
    expect((document.activeElement as HTMLElement).getAttribute('aria-label')).toBe(
      'Search active panel',
    );
  });

  it('routes Enter on an archive candidate through the shared store open operation', () => {
    const candidate = {
      name: 'photos.7z',
      relativePath: '/photos.7z',
      type: 'file' as const,
      size: 12,
      modifiedAt: null,
      extension: '7z',
      isReadOnly: false,
      isSymbolicLink: false,
      attributes: 'Normal',
      archiveFormatHint: 'sevenZip' as const,
      archiveRole: 'single' as const,
    };
    store.sources.set([source('downloads', 'Downloads')]);
    store.leftPanel.set(panel({
      tabs: [{
        id: 'tab',
        label: '/',
        location: { kind: 'filesystem', sourceId: 'downloads', path: '/' },
      }],
      entries: [candidate],
      cursorIndex: 0,
    }));

    fixture.componentInstance.execute({ type: 'open-cursor' });

    expect(store.openEntry).toHaveBeenCalledWith('left', expect.objectContaining(candidate));
  });

  it('blocks upload inside an archive even when the underlying source is writable', () => {
    store.sources.set([source('downloads', 'Downloads')]);
    store.leftPanel.set(panel({
      tabs: [{
        id: 'tab',
        label: 'photos.7z',
        location: {
          kind: 'archive',
          sourceId: 'downloads',
          archivePath: '/photos.7z',
          internalPath: '/',
        },
      }],
      archiveMetadata: { format: 'sevenZip', volumeCount: 1 },
    }));

    fixture.componentInstance.reviewUpload([new File(['one'], 'one.txt')]);

    expect(upload.open).not.toHaveBeenCalled();
    expect(fixture.componentInstance.commandStatus()).toContain('read-only archive');
  });
});

function panel(overrides: Partial<PanelState> = {}): PanelState {
  return {
    tabs: [{
      id: 'tab',
      label: '/',
      location: { kind: 'filesystem', sourceId: '', path: '/' },
    }],
    activeTabId: 'tab',
    cursorIndex: 0,
    selectedItems: new Set<string>(),
    selectionAnchor: null,
    sortColumn: 'name',
    sortDirection: 'ascending',
    filter: '',
    entries: [],
    loading: false,
    errorCode: null,
    errorDetail: null,
    archiveMetadata: null,
    requestToken: 0,
    ...overrides,
  };
}

function source(id: string, name: string, overrides: Partial<SourceDto> = {}): SourceDto {
  return {
    id,
    name,
    isAvailable: true,
    isReadOnly: false,
    totalBytes: 100,
    usedBytes: 25,
    freeBytes: 75,
    defaultLeft: false,
    defaultRight: false,
    ...overrides,
  };
}

function closedUploadState(): UploadState {
  return {
    phase: 'closed',
    context: null,
    files: [],
    limits: null,
    limitsPending: false,
    totalBytes: 0,
    preflightIssues: [],
    progressLoadedBytes: 0,
    progressTotalBytes: null,
    result: null,
    errorCode: null,
    errorMessage: null,
    requestToken: 0,
  };
}

function closedMultiRenameState(): MultiRenameState {
  return {
    open: false,
    context: null,
    rules: {
      nameMask: '[N]',
      extensionMask: '[E]',
      searchFor: '',
      replaceWith: '',
      useRegex: false,
      matchCase: false,
      replaceInExtension: false,
      caseMode: 'unchanged',
      counterStart: 1,
      counterStep: 1,
      counterDigits: 1,
    },
    preview: null,
    operation: null,
    previewPending: false,
    actionPending: false,
    disabledReason: null,
    errorCode: null,
    requestToken: 0,
  };
}
