import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Subject } from 'rxjs';
import { CommanderKeyboardService } from '../../../core/keyboard/commander-keyboard.service';
import {
  SourceDto,
  SourceManagementOperationDto,
  SystemUpdateStatusDto,
} from '../../../core/api/api.models';
import { CommanderStore } from '../../../core/state/commander-store';
import { PanelState } from '../../../core/state/commander.models';
import { SystemMetricsStore } from '../../../core/state/system-metrics-store';
import { UploadStore } from '../../../core/state/upload-store';
import { UploadState } from '../../../core/state/upload.models';
import { MultiRenameStore } from '../../../core/state/multi-rename-store';
import { MultiRenameState } from '../../../core/state/multi-rename.models';
import { SingleRenameStore } from '../../../core/state/single-rename-store';
import { SingleRenameState } from '../../../core/state/single-rename.models';
import {
  ArchiveExtractionState,
  ArchiveExtractionStore,
} from '../../../core/state/archive-extraction-store';
import { PwaService } from '../../../core/pwa/pwa.service';
import { AuthenticationStore } from '../../../core/auth/authentication-store';
import { ProtectedStateResetService } from '../../../core/auth/protected-state-reset.service';
import { FileOperationStore } from '../file-operations/file-operation.store';
import { TrashStore } from '../trash/trash.store';
import { SystemUpdateStore } from '../../../core/state/system-update.store';
import { SourceManagementStore } from '../../../core/state/source-management.store';
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
    reset: vi.fn(),
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
    reset: vi.fn(),
    refresh: vi.fn(() => Promise.resolve()),
    clearSelection: vi.fn(),
    createMultiRenameContext: vi.fn((_side?: 'left' | 'right'): any => null),
    createSingleRenameContext: vi.fn((_side?: 'left' | 'right'): any => null),
    refreshAfterRename: vi.fn(() => Promise.resolve()),
    captureFileOperationContext: vi.fn((_kind: 'copy' | 'move'): any => null),
    activatePanel: vi.fn(),
    moveCursor: vi.fn(),
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
    reset: vi.fn(),
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
  const singleRename = {
    state: signal<SingleRenameState>(closedSingleRenameState()),
    canExecute: signal(false),
    open: vi.fn((context: any) => singleRename.state.set({
      ...closedSingleRenameState(),
      open: true,
      context,
      newName: context.entry.name,
    })),
    close: vi.fn(() => singleRename.state.set(closedSingleRenameState())),
    setName: vi.fn(),
    execute: vi.fn(() => Promise.resolve(false)),
    setCompletionHandler: vi.fn(),
  };
  const archiveExtraction = {
    state: signal<ArchiveExtractionState>(closedArchiveExtractionState()),
    canExecute: signal(false),
    canCancel: signal(false),
    open: vi.fn(() => Promise.resolve()),
    close: vi.fn(),
    cancel: vi.fn(() => Promise.resolve()),
    setCompletionHandler: vi.fn(),
  };
  const fileOperations = {
    context: signal(null), destination: signal('/'), dialog: signal<'closed' | 'confirm' | 'progress'>('closed'),
    presentation: signal<'modal' | 'background'>('modal'), preview: signal(null),
    tasks: signal<readonly any[]>([]), conflictDecisions: signal(new Map()), busy: signal(false),
    error: signal<string | null>(null), activeOperationId: signal<string | null>(null),
    activeTask: signal(null), queuedCount: signal(0), canSubmit: signal(false),
    open: vi.fn(() => Promise.resolve()), restoreTasks: vi.fn(() => Promise.resolve()),
    setTerminalHandler: vi.fn(), resetProtectedState: vi.fn(), background: vi.fn(),
    restoreProgress: vi.fn(), cancel: vi.fn(), acknowledge: vi.fn(), setDestination: vi.fn(),
    setConflictDecision: vi.fn(), applyDecisionToRemaining: vi.fn(), submit: vi.fn(),
    closeConfirmation: vi.fn(),
  };
  const trash = {
    sourceFilter: signal<string | null>(null), entries: signal<readonly any[]>([]),
    selection: signal<ReadonlySet<string>>(new Set()), restorePreview: signal(null),
    restoreConflictDecisions: signal(new Map()), deletePreview: signal(null),
    deleteRequest: signal(null), busy: signal(false), error: signal<string | null>(null),
    canSubmitRestore: signal(false), load: vi.fn(), setSourceFilter: vi.fn(),
    toggleSelection: vi.fn(), selectAll: vi.fn(), clearSelection: vi.fn(),
    previewSelectedRestore: vi.fn(), setRestoreConflictDecision: vi.fn(), submitRestore: vi.fn(),
    previewDelete: vi.fn(() => Promise.resolve()), changeDeleteMode: vi.fn(), submitDelete: vi.fn(),
    permanentlyDeleteSelected: vi.fn(), emptyTrash: vi.fn(), clearDeletePreview: vi.fn(),
    resetProtectedState: vi.fn(),
  };
  const pwa = {
    canInstall: signal(false),
    online: signal(true),
    updateReady: signal(false),
    installing: signal(false),
    error: signal<string | null>(null),
    install: vi.fn(() => Promise.resolve()),
    reloadForUpdate: vi.fn(),
    dismissUpdate: vi.fn(),
  };
  const authentication = {
    state: signal({
      phase: 'authenticated' as const,
      username: 'dragos',
      pending: false,
      errorCode: null,
      errorMessage: null,
    }),
    logout: vi.fn(() => Promise.resolve()),
    changePassword: vi.fn(() => Promise.resolve()),
  };
  const systemUpdate = {
    status: signal<SystemUpdateStatusDto | null>(null),
    reconnecting: signal(false),
    error: signal(null),
    start: vi.fn(() => Promise.resolve()),
    check: vi.fn(() => Promise.resolve()),
    apply: vi.fn(() => Promise.resolve()),
    dismissTerminal: vi.fn(),
    reset: vi.fn(),
    overlayVisible: signal(false),
  };
  const sourceManagement = {
    capability: signal({
      supported: true,
      reasonCode: 'supported',
      detail: 'Source management is available.',
    }),
    capabilityPending: signal(false),
    canOpen: signal(true),
    disabledReason: signal<string | null>(null),
    dialogOpen: signal(false),
    pending: signal(false),
    reconnecting: signal(false),
    operation: signal<SourceManagementOperationDto | null>(null),
    error: signal(null),
    catalogRefreshed: signal(false),
    terminal: signal(false),
    start: vi.fn(() => Promise.resolve()),
    open: vi.fn(() => sourceManagement.dialogOpen.set(true)),
    close: vi.fn(() => sourceManagement.dialogOpen.set(false)),
    submit: vi.fn(() => Promise.resolve()),
    reset: vi.fn(),
  };

  beforeEach(async () => {
    vi.clearAllMocks();
    localStorage.clear();
    document.documentElement.removeAttribute('data-theme');
    store.sources.set([]);
    store.leftPanel.set(panel());
    store.rightPanel.set(panel());
    store.activePanel.set('left');
    upload.state.set(closedUploadState());
    multiRename.state.set(closedMultiRenameState());
    singleRename.state.set(closedSingleRenameState());
    archiveExtraction.state.set(closedArchiveExtractionState());
    fileOperations.dialog.set('closed');
    fileOperations.presentation.set('modal');
    fileOperations.tasks.set([]);
    fileOperations.activeTask.set(null);
    trash.deletePreview.set(null);
    store.captureFileOperationContext.mockReturnValue(null);
    store.createSingleRenameContext.mockReturnValue(null);
    pwa.canInstall.set(false);
    pwa.online.set(true);
    pwa.updateReady.set(false);
    pwa.installing.set(false);
    pwa.error.set(null);
    systemUpdate.status.set(null);
    systemUpdate.reconnecting.set(false);
    systemUpdate.overlayVisible.set(false);
    sourceManagement.dialogOpen.set(false);
    sourceManagement.capability.set({
      supported: true,
      reasonCode: 'supported',
      detail: 'Source management is available.',
    });
    sourceManagement.canOpen.set(true);
    sourceManagement.disabledReason.set(null);
    await TestBed.configureTestingModule({
      imports: [CommanderShellComponent],
      providers: [
        { provide: CommanderKeyboardService, useValue: keyboard },
        { provide: CommanderStore, useValue: store },
        { provide: SystemMetricsStore, useValue: metrics },
        { provide: UploadStore, useValue: upload },
        { provide: MultiRenameStore, useValue: multiRename },
        { provide: SingleRenameStore, useValue: singleRename },
        { provide: ArchiveExtractionStore, useValue: archiveExtraction },
        { provide: FileOperationStore, useValue: fileOperations },
        { provide: TrashStore, useValue: trash },
        { provide: PwaService, useValue: pwa },
        { provide: AuthenticationStore, useValue: authentication },
        { provide: SystemUpdateStore, useValue: systemUpdate },
        { provide: SourceManagementStore, useValue: sourceManagement },
      ],
    }).compileComponents();
    fixture = TestBed.createComponent(CommanderShellComponent);
    fixture.detectChanges();
  });

  afterEach(() => document.documentElement.removeAttribute('data-theme'));

  it('places the widget last, opens details, and starts only one polling lifecycle', () => {
    const actions = fixture.nativeElement.querySelector('.top-actions');
    expect(actions.lastElementChild?.tagName).toBe('APP-SYSTEM-METRICS-WIDGET');
    expect(actions.lastElementChild?.previousElementSibling?.tagName).toBe(
      'APP-SYSTEM-UPDATE-BUTTON',
    );
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

  it('advertises Shift+Arrow range selection in the shortcut hint and command reference', () => {
    fixture.componentInstance.menuOpen.set(true);
    fixture.detectChanges();
    const hint = fixture.nativeElement.querySelector('.shortcut-hint') as HTMLElement;
    const commands = fixture.nativeElement.querySelector('.command-menu') as HTMLElement;

    expect(hint.textContent).toContain('Shift+↑/↓ range select');
    expect(commands.textContent).toContain('Shift+↑/↓');
  });

  it('places an enabled available-update control immediately before telemetry', () => {
    systemUpdate.status.set(systemUpdateStatus({
      phase: 'available',
      canApply: true,
      updateAvailable: true,
      targetVersion: 'v1.4.0',
    }));
    fixture.detectChanges();
    const actions = fixture.nativeElement.querySelector('.top-actions') as HTMLElement;
    const update = actions.querySelector('app-system-update-button') as HTMLElement;
    const metricsWidget = actions.querySelector('app-system-metrics-widget') as HTMLElement;

    expect(update.nextElementSibling).toBe(metricsWidget);
    expect(update.querySelector('button')?.getAttribute('aria-label')).toBe(
      'Update available: v1.4.0',
    );
    expect(systemUpdate.start).toHaveBeenCalledOnce();
  });

  it('opens immutable confirmation and delegates one confirmed Apply', () => {
    systemUpdate.status.set(systemUpdateStatus({
      phase: 'available',
      canApply: true,
      updateAvailable: true,
      targetVersion: 'v1.4.0',
    }));
    fixture.detectChanges();

    (fixture.nativeElement.querySelector(
      '[data-testid="system-update-trigger"]',
    ) as HTMLButtonElement).click();
    fixture.detectChanges();
    const confirm = [...fixture.nativeElement.querySelectorAll('button')]
      .find((button: HTMLButtonElement) => button.textContent?.includes('Update ReachCommander'))!;
    confirm.click();

    expect(systemUpdate.apply).toHaveBeenCalledOnce();
  });

  it('places an accessible theme selector before account and metrics controls', () => {
    const actions = fixture.nativeElement.querySelector('.top-actions') as HTMLElement;
    const selector = fixture.nativeElement.querySelector(
      '[data-testid="theme-selector"]',
    ) as HTMLSelectElement;

    expect(selector).not.toBeNull();
    expect(selector.getAttribute('aria-label')).toBe('Theme');
    expect([...selector.options].map(({ value, text }) => [value, text])).toEqual([
      ['default', 'Modern'],
      ['norton', 'Norton'],
      ['windows95', 'Windows 95'],
    ]);
    expect(selector.value).toBe('default');
    expect(selector.parentElement?.nextElementSibling?.tagName).toBe('APP-ACCOUNT-MENU');

    selector.value = 'windows95';
    selector.dispatchEvent(new Event('change'));
    fixture.detectChanges();

    expect(document.documentElement.dataset['theme']).toBe('windows95');
    expect(selector.value).toBe('windows95');
  });

  it('loads source-management capability and opens the blocking Add source dialog', () => {
    expect(sourceManagement.start).toHaveBeenCalledOnce();
    const trigger = fixture.nativeElement.querySelector(
      '[data-testid="toolbar-add-source"]',
    ) as HTMLButtonElement;
    expect(trigger).not.toBeNull();

    trigger.click();
    fixture.detectChanges();

    expect(sourceManagement.open).toHaveBeenCalledOnce();
    expect(fixture.nativeElement.querySelector('[data-testid="source-management-dialog"]'))
      .not.toBeNull();
  });

  it('stops polling when the shell is destroyed', () => {
    fixture.destroy();
    expect(metrics.stop).toHaveBeenCalledOnce();
  });

  it('clears every protected workspace store when authentication locks', () => {
    TestBed.inject(ProtectedStateResetService).reset();

    expect(store.reset).toHaveBeenCalledOnce();
    expect(metrics.reset).toHaveBeenCalledOnce();
    expect(upload.reset).toHaveBeenCalledOnce();
    expect(multiRename.close).toHaveBeenCalledOnce();
    expect(archiveExtraction.close).toHaveBeenCalledOnce();
    expect(fileOperations.resetProtectedState).toHaveBeenCalledOnce();
    expect(trash.resetProtectedState).toHaveBeenCalledOnce();
  });

  it('handles Escape by closing metrics before commander state changes', () => {
    fixture.componentInstance.openMetrics();

    fixture.componentInstance.execute({ type: 'escape' });

    expect(fixture.componentInstance.metricsOpen()).toBe(false);
    expect(metrics.start).toHaveBeenCalledOnce();
  });

  it('routes shifted cursor movement to the active pane with range-selection intent', () => {
    fixture.componentInstance.execute({
      type: 'move-cursor',
      amount: 1,
      extendSelection: true,
    });

    expect(store.moveCursor).toHaveBeenCalledWith('left', 1, true);
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

  it('routes F5 and the toolbar through one captured archive extraction context', () => {
    const candidate = {
      name: 'photos.7z', relativePath: '/photos.7z', type: 'file' as const, size: 12,
      modifiedAt: null, extension: '7z', isReadOnly: false, isSymbolicLink: false,
      attributes: 'Normal', archiveFormatHint: 'sevenZip' as const, archiveRole: 'single' as const,
    };
    store.sources.set([source('downloads', 'Downloads'), source('media', 'Media')]);
    store.leftPanel.set(panel({
      tabs: [{ id: 'left', label: '/', location: { kind: 'filesystem', sourceId: 'downloads', path: '/' } }],
      activeTabId: 'left', entries: [candidate], cursorIndex: 0,
    }));
    store.rightPanel.set(panel({
      tabs: [{ id: 'right', label: 'Photos', location: { kind: 'filesystem', sourceId: 'media', path: '/Photos' } }],
      activeTabId: 'right',
    }));
    fixture.detectChanges();

    const f5 = fixture.nativeElement.querySelector('[data-key="F5"]') as HTMLButtonElement;
    expect(f5.disabled).toBe(false);
    expect(f5.textContent).toContain('Extract');
    fixture.componentInstance.handleFunctionKey('F5');

    expect(archiveExtraction.open).toHaveBeenCalledWith(expect.objectContaining({
      archivePath: '/photos.7z', extractAll: true, destinationPath: '/Photos',
    }));

    archiveExtraction.open.mockClear();
    (fixture.nativeElement.querySelector('[data-testid="toolbar-extract"]') as HTMLButtonElement).click();
    expect(archiveExtraction.open).toHaveBeenCalledOnce();
  });

  it('keeps physical F5 reserved when the focused filesystem row is not an archive', () => {
    store.sources.set([source('downloads', 'Downloads'), source('media', 'Media')]);
    store.leftPanel.set(panel({
      tabs: [{ id: 'left', label: '/', location: { kind: 'filesystem', sourceId: 'downloads', path: '/' } }],
      activeTabId: 'left', entries: [{
        name: 'notes.txt', relativePath: '/notes.txt', type: 'file', size: 12,
        modifiedAt: null, extension: 'txt', isReadOnly: false, isSymbolicLink: false,
        attributes: 'Normal', archiveFormatHint: null, archiveRole: null,
      }], cursorIndex: 0,
    }));
    store.rightPanel.set(panel({
      tabs: [{ id: 'right', label: 'Media', location: { kind: 'filesystem', sourceId: 'media', path: '/' } }],
      activeTabId: 'right',
    }));
    fixture.detectChanges();

    fixture.componentInstance.handleFunctionKey('F5');

    expect(archiveExtraction.open).not.toHaveBeenCalled();
    expect(fixture.componentInstance.commandStatus()).toContain('Select or focus');
  });

  it('maps filesystem F5 through F8 to Copy, Move, MkDir, and Delete', () => {
    store.sources.set([source('downloads', 'Downloads'), source('media', 'Media')]);
    store.leftPanel.set(panel({
      tabs: [{ id: 'left', label: 'Files', location: { kind: 'filesystem', sourceId: 'downloads', path: '/Files' } }],
      activeTabId: 'left', entries: [{
        name: 'one.txt', relativePath: '/Files/one.txt', type: 'file', size: 1,
        modifiedAt: null, extension: 'txt', isReadOnly: false, isSymbolicLink: false,
        attributes: '', archiveFormatHint: null, archiveRole: null,
      }], cursorIndex: 0,
    }));
    store.rightPanel.set(panel({
      tabs: [{ id: 'right', label: 'Target', location: { kind: 'filesystem', sourceId: 'media', path: '/Target' } }],
      activeTabId: 'right',
    }));
    store.captureFileOperationContext.mockImplementation((kind: 'copy' | 'move') => ({
      kind, sourceId: 'downloads', logicalPaths: ['/Files/one.txt'], destinationSourceId: 'media',
      destinationLogicalDirectory: '/Target', selectedNames: ['one.txt'], knownTotalBytes: 1,
    }));
    store.createMultiRenameContext.mockReturnValue({
      panelSide: 'left', sourceId: 'downloads', sourceName: 'Downloads', directoryPath: '/Files',
      entries: store.leftPanel().entries, isAvailable: true, isReadOnly: false,
    });

    fixture.componentInstance.handleFunctionKey('F5');
    fixture.componentInstance.handleFunctionKey('F6');
    fixture.componentInstance.handleFunctionKey('F7');
    fixture.componentInstance.handleFunctionKey('F8');

    expect(fileOperations.open).toHaveBeenNthCalledWith(1, 'copy', expect.any(Object));
    expect(fileOperations.open).toHaveBeenNthCalledWith(2, 'move', expect.any(Object));
    expect(fixture.componentInstance.createDirectoryContext()).toEqual(expect.objectContaining({ parentLogicalPath: '/Files' }));
    expect(trash.previewDelete).toHaveBeenCalledWith({
      sourceId: 'downloads', logicalPaths: ['/Files/one.txt'], mode: 'trash',
    });
  });

  it('opens single rename with F4 for the focused row and blocks unrelated commands', () => {
    const context = {
      panelSide: 'left' as const,
      sourceId: 'downloads',
      sourceName: 'Downloads',
      directoryPath: '/Files',
      entry: {
        name: 'one.txt', relativePath: '/Files/one.txt', type: 'file' as const, size: 1,
        modifiedAt: null, extension: 'txt', isReadOnly: false, isSymbolicLink: false,
        attributes: '', archiveFormatHint: null, archiveRole: null,
      },
      isAvailable: true,
      isReadOnly: false,
    };
    store.sources.set([source('downloads', 'Downloads')]);
    store.leftPanel.set(panel({
      tabs: [{ id: 'left', label: 'Files', location: { kind: 'filesystem', sourceId: 'downloads', path: '/Files' } }],
      activeTabId: 'left', entries: [context.entry], cursorIndex: 0,
    }));
    store.createSingleRenameContext.mockReturnValue(context);
    fixture.detectChanges();

    expect((fixture.nativeElement.querySelector('[data-key="F4"]') as HTMLButtonElement).disabled)
      .toBe(false);
    fixture.componentInstance.handleFunctionKey('F4');
    fixture.detectChanges();

    expect(singleRename.open).toHaveBeenCalledWith(context);
    expect(fixture.nativeElement.querySelector('[data-testid="single-rename-dialog"]')).not.toBeNull();
    fixture.componentInstance.execute({ type: 'switch-panel' });
    expect(store.activatePanel).not.toHaveBeenCalled();
  });

  it('disables F4 for symbolic links with an exact reason', () => {
    store.sources.set([source('downloads', 'Downloads')]);
    store.createSingleRenameContext.mockReturnValue({
      panelSide: 'left', sourceId: 'downloads', sourceName: 'Downloads', directoryPath: '/',
      entry: {
        name: 'shortcut', relativePath: '/shortcut', type: 'file', size: 1,
        modifiedAt: null, extension: null, isReadOnly: false, isSymbolicLink: true,
        attributes: '', archiveFormatHint: null, archiveRole: null,
      },
      isAvailable: true, isReadOnly: false,
    });
    fixture.detectChanges();

    const rename = fixture.nativeElement.querySelector('[data-key="F4"]') as HTMLButtonElement;
    expect(rename.disabled).toBe(true);
    expect(rename.title).toBe('Symbolic links cannot be renamed.');
  });

  it('refreshes matching panels, closes rename, and restores its F4 opener after completion', async () => {
    const context = {
      panelSide: 'left' as const,
      sourceId: 'downloads',
      sourceName: 'Downloads',
      directoryPath: '/',
      entry: {
        name: 'old.txt', relativePath: '/old.txt', type: 'file' as const, size: 1,
        modifiedAt: null, extension: 'txt', isReadOnly: false, isSymbolicLink: false,
        attributes: '', archiveFormatHint: null, archiveRole: null,
      },
      isAvailable: true,
      isReadOnly: false,
    };
    store.sources.set([source('downloads', 'Downloads')]);
    store.createSingleRenameContext.mockReturnValue(context);
    fixture.detectChanges();
    const opener = fixture.nativeElement.querySelector('[data-key="F4"]') as HTMLButtonElement;
    opener.focus();
    fixture.componentInstance.handleFunctionKey('F4');
    const completion = { context, newLogicalPath: '/renamed.txt' };
    const handler = singleRename.setCompletionHandler.mock.calls.at(-1)?.[0];

    await handler(completion);
    fixture.detectChanges();
    await Promise.resolve();

    expect(store.refreshAfterRename).toHaveBeenCalledWith(completion);
    expect(singleRename.close).toHaveBeenCalled();
    expect(document.activeElement).toBe(opener);
  });

  it('keeps archive extraction ahead of Copy and blocks commander movement under file modals', () => {
    fileOperations.dialog.set('confirm');
    fixture.componentInstance.execute({ type: 'switch-panel' });
    expect(store.activatePanel).not.toHaveBeenCalled();

    fileOperations.dialog.set('progress');
    fileOperations.presentation.set('background');
    fixture.componentInstance.execute({ type: 'switch-panel' });
    expect(store.activatePanel).toHaveBeenCalledOnce();
  });

  it('allows Copy from read-only sources while disabling Move, MkDir, and Delete', () => {
    store.sources.set([source('archive', 'Archive', { isReadOnly: true }), source('media', 'Media')]);
    store.leftPanel.set(panel({
      tabs: [{ id: 'left', label: '/', location: { kind: 'filesystem', sourceId: 'archive', path: '/' } }],
      activeTabId: 'left', entries: [{
        name: 'one.iso', relativePath: '/one.iso', type: 'file', size: 1, modifiedAt: null,
        extension: 'iso', isReadOnly: true, isSymbolicLink: false, attributes: '',
        archiveFormatHint: null, archiveRole: null,
      }], cursorIndex: 0,
    }));
    store.rightPanel.set(panel({
      tabs: [{ id: 'right', label: '/', location: { kind: 'filesystem', sourceId: 'media', path: '/' } }],
      activeTabId: 'right',
    }));
    store.createMultiRenameContext.mockReturnValue({
      panelSide: 'left', sourceId: 'archive', sourceName: 'Archive', directoryPath: '/',
      entries: store.leftPanel().entries, isAvailable: true, isReadOnly: true,
    });
    store.captureFileOperationContext.mockImplementation((kind: 'copy' | 'move') =>
      kind === 'copy' ? {
        kind, sourceId: 'archive', logicalPaths: ['/one.iso'], destinationSourceId: 'media',
        destinationLogicalDirectory: '/', selectedNames: ['one.iso'], knownTotalBytes: 1,
      } : null,
    );

    const availability = fixture.componentInstance.fileCommandAvailability();

    expect(availability.copy.enabled).toBe(true);
    expect(availability.move.reason).toContain('read-only');
    expect(availability.createDirectory.enabled).toBe(false);
    expect(availability.delete.enabled).toBe(false);
  });

  it('refreshes affected visible panels from a terminal file-operation outcome', async () => {
    store.sources.set([source('downloads', 'Downloads'), source('media', 'Media')]);
    store.leftPanel.set(panel({
      tabs: [{ id: 'left', label: '/', location: { kind: 'filesystem', sourceId: 'downloads', path: '/' } }],
      activeTabId: 'left',
    }));
    store.rightPanel.set(panel({
      tabs: [{ id: 'right', label: '/', location: { kind: 'filesystem', sourceId: 'media', path: '/' } }],
      activeTabId: 'right',
    }));
    const handler = fileOperations.setTerminalHandler.mock.calls.at(-1)?.[0];

    await handler({
      operationId: 'copy', kind: 'copy', phase: 'completed', queuePosition: 0,
      createdAt: '', updatedAt: '', progress: {
        currentLogicalName: null, completedItems: 1, totalItems: 1, completedBytes: 1,
        totalBytes: 1, percentage: 100, bytesPerSecond: null, elapsed: '00:00:01',
        estimatedRemaining: null,
      }, warnings: [], acknowledged: false, outcomes: [{
        sourceId: 'downloads', sourceLogicalPath: '/one.txt', destinationSourceId: 'media',
        destinationLogicalPath: '/one.txt', result: 'completed', errorCode: null, detail: null,
      }],
    }, null);

    expect(store.refresh).toHaveBeenCalledWith('left');
    expect(store.refresh).toHaveBeenCalledWith('right');
  });

  it('restores focus to the F5 opener after the extraction dialog closes', async () => {
    const candidate = {
      name: 'photos.zip', relativePath: '/photos.zip', type: 'file' as const, size: 12,
      modifiedAt: null, extension: 'zip', isReadOnly: false, isSymbolicLink: false,
      attributes: 'Normal', archiveFormatHint: 'zip' as const, archiveRole: 'single' as const,
    };
    store.sources.set([source('downloads', 'Downloads'), source('media', 'Media')]);
    store.leftPanel.set(panel({
      tabs: [{ id: 'left', label: '/', location: { kind: 'filesystem', sourceId: 'downloads', path: '/' } }],
      activeTabId: 'left', entries: [candidate], cursorIndex: 0,
    }));
    store.rightPanel.set(panel({
      tabs: [{ id: 'right', label: 'Media', location: { kind: 'filesystem', sourceId: 'media', path: '/' } }],
      activeTabId: 'right',
    }));
    fixture.detectChanges();
    const opener = fixture.nativeElement.querySelector('[data-key="F5"]') as HTMLButtonElement;
    opener.focus();
    fixture.componentInstance.handleFunctionKey('F5');
    const context = fixture.componentInstance.extractionContext().context!;
    archiveExtraction.state.set({
      ...closedArchiveExtractionState(), phase: 'review', context,
    });
    archiveExtraction.close.mockImplementationOnce(() =>
      archiveExtraction.state.set(closedArchiveExtractionState()),
    );
    fixture.detectChanges();

    fixture.componentInstance.closeArchiveExtraction();
    fixture.detectChanges();
    await Promise.resolve();

    expect(document.activeElement).toBe(opener);
  });

  it('blocks commander commands while extraction is modal and refreshes both captured panels', async () => {
    archiveExtraction.state.set({
      ...closedArchiveExtractionState(),
      phase: 'running',
    });
    fixture.componentInstance.execute({ type: 'switch-panel' });
    expect(store.activatePanel).not.toHaveBeenCalled();

    const handler = archiveExtraction.setCompletionHandler.mock.calls.at(-1)?.[0];
    expect(handler).toBeTypeOf('function');
    await handler('left', 'right');
    expect(store.refresh).toHaveBeenCalledWith('left');
    expect(store.refresh).toHaveBeenCalledWith('right');
  });

  it('shows the supported install action and delegates one click', () => {
    pwa.canInstall.set(true);
    fixture.detectChanges();

    const button = fixture.nativeElement.querySelector(
      '[data-testid="install-app"]',
    ) as HTMLButtonElement;
    expect(button).not.toBeNull();

    button.click();

    expect(pwa.install).toHaveBeenCalledOnce();
  });

  it('shows an accessible offline notice without stale-data claims', () => {
    pwa.online.set(false);
    fixture.detectChanges();

    const notice = fixture.nativeElement.querySelector(
      '[data-testid="connection-notice"]',
    ) as HTMLElement;
    expect(notice.getAttribute('role')).toBe('status');
    expect(notice.textContent).toContain('offline');
    expect(notice.textContent).toContain(
      'Live file data and operations require the server',
    );
  });

  it('reloads or dismisses a ready update only from the notice actions', () => {
    pwa.updateReady.set(true);
    fixture.detectChanges();

    (fixture.nativeElement.querySelector(
      '[data-testid="reload-update"]',
    ) as HTMLButtonElement).click();
    expect(pwa.reloadForUpdate).toHaveBeenCalledOnce();

    (fixture.nativeElement.querySelector(
      '[data-testid="dismiss-update"]',
    ) as HTMLButtonElement).click();
    expect(pwa.dismissUpdate).toHaveBeenCalledOnce();
  });

  it('reports an unreachable server and retries initialization explicitly', async () => {
    store.initialize.mockRejectedValueOnce(new Error('unreachable'));

    await fixture.componentInstance.retryInitialization();
    fixture.detectChanges();

    expect(
      fixture.nativeElement.querySelector('[data-testid="connection-notice"]').textContent,
    ).toContain('server is unavailable');

    store.initialize.mockResolvedValueOnce(undefined);
    (fixture.nativeElement.querySelector(
      '[data-testid="retry-connection"]',
    ) as HTMLButtonElement).click();
    await fixture.whenStable();

    expect(store.initialize).toHaveBeenCalledTimes(3);
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

function closedSingleRenameState(): SingleRenameState {
  return {
    open: false,
    context: null,
    newName: '',
    preview: null,
    operation: null,
    previewPending: false,
    actionPending: false,
    errorCode: null,
    requestToken: 0,
  };
}

function closedArchiveExtractionState(): ArchiveExtractionState {
  return {
    phase: 'closed', context: null, preview: null, operation: null, error: null, requestToken: 0,
  };
}

function systemUpdateStatus(
  overrides: Partial<SystemUpdateStatusDto> = {},
): SystemUpdateStatusDto {
  return {
    protocolVersion: 1,
    supported: true,
    channel: 'stable',
    currentVersion: 'v1.3.0',
    targetVersion: null,
    phase: 'current',
    progressStage: null,
    updateAvailable: false,
    canApply: false,
    reasonCode: 'up_to_date',
    detail: 'ReachCommander is up to date.',
    operationId: null,
    lastCheckedAt: '2026-08-25T10:00:00Z',
    updatedAt: '2026-08-25T10:00:00Z',
    trace: null,
    ...overrides,
  };
}
