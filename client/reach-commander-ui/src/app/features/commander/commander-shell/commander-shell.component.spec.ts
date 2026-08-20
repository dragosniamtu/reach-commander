import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Subject } from 'rxjs';
import { CommanderKeyboardService } from '../../../core/keyboard/commander-keyboard.service';
import { CommanderStore } from '../../../core/state/commander-store';
import { PanelState } from '../../../core/state/commander.models';
import { SystemMetricsStore } from '../../../core/state/system-metrics-store';
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
    sources: signal([]),
    leftPanel: signal(panel()),
    rightPanel: signal(panel()),
    activePanel: signal<'left' | 'right'>('left'),
    initialize: vi.fn(() => Promise.resolve()),
  };

  beforeEach(async () => {
    vi.clearAllMocks();
    await TestBed.configureTestingModule({
      imports: [CommanderShellComponent],
      providers: [
        { provide: CommanderKeyboardService, useValue: keyboard },
        { provide: CommanderStore, useValue: store },
        { provide: SystemMetricsStore, useValue: metrics },
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
});

function panel(): PanelState {
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
  };
}
