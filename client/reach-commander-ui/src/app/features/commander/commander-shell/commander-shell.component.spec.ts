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
    state: signal({ snapshot: null, pending: false, errorCode: null, requestToken: 0, nowEpochMilliseconds: Date.now() }),
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
  };
  const upload = {
    state: signal<UploadState>(closedUploadState()),
    isPending: vi.fn(() => false),
    open: vi.fn(),
    start: vi.fn(() => true),
    cancel: vi.fn(() => true),
    close: vi.fn(() => true),
  };

  beforeEach(async () => {
    vi.clearAllMocks();
    store.sources.set([]);
    store.leftPanel.set(panel());
    store.rightPanel.set(panel());
    store.activePanel.set('left');
    upload.state.set(closedUploadState());
    await TestBed.configureTestingModule({
      imports: [CommanderShellComponent],
      providers: [
        { provide: CommanderKeyboardService, useValue: keyboard },
        { provide: CommanderStore, useValue: store },
        { provide: SystemMetricsStore, useValue: metrics },
        { provide: UploadStore, useValue: upload },
      ],
    }).compileComponents();
    fixture = TestBed.createComponent(CommanderShellComponent);
    fixture.detectChanges();
  });

  it('places the widget last, opens details, and starts only one polling lifecycle', () => {
    const actions = fixture.nativeElement.querySelector('.top-actions');
    expect(actions.lastElementChild?.tagName).toBe('APP-SYSTEM-METRICS-WIDGET');
    expect(metrics.start).toHaveBeenCalledOnce();

    (fixture.nativeElement.querySelector('[data-testid="system-metrics-trigger"]') as HTMLButtonElement).click();
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
    store.sources.set([
      source('downloads', 'Downloads'),
      source('media', 'Media'),
    ]);
    store.leftPanel.set(panel({
      sourceId: 'downloads',
      tabs: [{ id: 'left-tab', label: 'Complete', sourceId: 'downloads', path: '/Complete' }],
      activeTabId: 'left-tab',
      filter: '*.zip',
      selectedItems: new Set(['/existing.zip']),
    }));
    store.rightPanel.set(panel({
      sourceId: 'media',
      tabs: [{ id: 'right-tab', label: 'Movies', sourceId: 'media', path: '/Movies' }],
      activeTabId: 'right-tab',
    }));
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
    store.leftPanel.set(panel({
      sourceId: 'archive',
      tabs: [{ id: 'tab', label: '/', sourceId: 'archive', path: '/' }],
    }));

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
});

function panel(overrides: Partial<PanelState> = {}): PanelState {
  return {
    sourceId: '',
    tabs: [{ id: 'tab', label: '/', sourceId: '', path: '/' }],
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
    requestToken: 0,
    ...overrides,
  };
}

function source(
  id: string,
  name: string,
  overrides: Partial<SourceDto> = {},
): SourceDto {
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
