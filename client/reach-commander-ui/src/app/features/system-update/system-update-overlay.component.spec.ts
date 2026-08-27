import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { SystemUpdateStatusDto } from '../../core/api/api.models';
import { SystemUpdateOverlayComponent } from './system-update-overlay.component';
import { SystemUpdateSupportBundleService } from './system-update-support-bundle.service';

describe('SystemUpdateOverlayComponent', () => {
  let fixture: ComponentFixture<SystemUpdateOverlayComponent>;
  let downloadDiagnostics: ReturnType<typeof vi.fn>;
  const diagnosticsPending = signal(false);
  const diagnosticsError = signal<string | null>(null);

  beforeEach(async () => {
    TestBed.configureTestingModule({
      imports: [SystemUpdateOverlayComponent],
    });
    TestBed.overrideComponent(SystemUpdateOverlayComponent, {
      set: {
        providers: [
          {
            provide: SystemUpdateSupportBundleService,
            useFactory: () => ({
              pending: diagnosticsPending.asReadonly(),
              error: diagnosticsError.asReadonly(),
              download: downloadDiagnostics,
            }),
          },
        ],
      },
    });
    await TestBed.compileComponents();
    downloadDiagnostics = vi.fn(() => Promise.resolve());
    diagnosticsPending.set(false);
    diagnosticsError.set(null);
    fixture = TestBed.createComponent(SystemUpdateOverlayComponent);
  });

  it('keeps a blocking reconnect state while the backend restarts', () => {
    fixture.componentRef.setInput('status', status({ phase: 'applying' }));
    fixture.componentRef.setInput('reconnecting', true);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[aria-modal="true"]')).not.toBeNull();
    expect(fixture.nativeElement.textContent).toContain('Reconnecting');
    expect(fixture.nativeElement.querySelector('.return-button')).toBeNull();
  });

  it('renders two decorative progress rings while applying', () => {
    fixture.componentRef.setInput('status', status({ phase: 'applying' }));
    fixture.componentRef.setInput('reconnecting', false);
    fixture.detectChanges();

    const spinner = fixture.nativeElement.querySelector('.spinner') as HTMLElement;
    expect(spinner.getAttribute('aria-hidden')).toBe('true');
    expect(spinner.querySelectorAll(':scope > i')).toHaveLength(2);
    expect(fixture.nativeElement.textContent).toContain('Updating ReachCommander');
  });

  it('renders and announces the active detailed update step', () => {
    fixture.componentRef.setInput('status', status({ progressStage: 'installing' }));
    fixture.componentRef.setInput('reconnecting', false);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('ol[aria-label="Update progress"]')).not.toBeNull();
    expect(
      fixture.nativeElement.querySelector('[data-step-state="active"]')?.textContent,
    ).toContain('Installing update');
    expect(fixture.nativeElement.querySelector('[aria-live="polite"]')?.textContent).toContain(
      'Installing update',
    );
  });

  it('keeps semantic progress text and states available without relying on motion', () => {
    fixture.componentRef.setInput('status', status({ progressStage: 'healthChecking' }));
    fixture.detectChanges();

    const active = fixture.nativeElement.querySelector('[data-step-state="active"]');
    expect(active?.textContent).toContain('Checking system health');
    expect(active?.getAttribute('data-step-state')).toBe('active');
  });

  it('separates automatic recovery progress from the standard update path', () => {
    fixture.componentRef.setInput('status', status({ progressStage: 'restoring' }));
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('Recovering previous version');
    expect(
      fixture.nativeElement.querySelector('ol[aria-label="Recovery progress"]'),
    ).not.toBeNull();
    expect(
      fixture.nativeElement.querySelector('[data-step-state="active"]')?.textContent,
    ).toContain('Restoring previous version');
  });

  it.each([
    ['rolledBack', 'previous version was restored'],
    ['failed', 'sudo reachcommander doctor'],
  ] as const)('shows dismissible %s guidance', (phase, text) => {
    const dismissed = vi.spyOn(fixture.componentInstance.dismissed, 'emit');
    fixture.componentRef.setInput('status', status({ phase }));
    fixture.componentRef.setInput('reconnecting', false);
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent.toLowerCase()).toContain(text);
    (fixture.nativeElement.querySelector('.return-button') as HTMLButtonElement).click();
    expect(dismissed).toHaveBeenCalledOnce();
  });

  it('downloads sanitized diagnostics without closing the blocking update screen', () => {
    fixture.componentRef.setInput('status', status({ phase: 'applying' }));
    fixture.detectChanges();

    const button = fixture.nativeElement.querySelector(
      '.download-diagnostics',
    ) as HTMLButtonElement;
    expect(button.textContent).toContain('Download diagnostics');

    button.click();

    expect(downloadDiagnostics).toHaveBeenCalledOnce();
    expect(fixture.nativeElement.querySelector('[aria-modal="true"]')).not.toBeNull();
  });

  it('keeps fixed CLI guidance visible if browser download fails', () => {
    diagnosticsError.set(
      'Diagnostics could not be downloaded. Run sudo reachcommander support-bundle.',
    );
    fixture.componentRef.setInput('status', status({ phase: 'failed' }));
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('sudo reachcommander support-bundle');
    expect(fixture.nativeElement.querySelector('.download-diagnostics')).not.toBeNull();
  });

  it('shows the root-only trace and health commands after an update failure', () => {
    fixture.componentRef.setInput('status', status({ phase: 'failed' }));
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('sudo reachcommander update-log');
    expect(fixture.nativeElement.textContent).toContain('sudo reachcommander doctor');
  });

  it('opens keyboard-accessible technical details after sixty silent seconds', () => {
    fixture.componentRef.setInput(
      'status',
      status({
        protocolVersion: 3,
        trace: {
          startedAt: '2000-01-01T00:00:00Z',
          elapsedSeconds: 65,
          lastActivityAt: null,
          events: [
            {
              sequence: 1,
              timestamp: '2000-01-01T00:00:00Z',
              elapsedSeconds: 0,
              code: 'operationAccepted',
              stage: null,
              outcome: 'started',
            },
          ],
        },
      }),
    );
    fixture.detectChanges();

    const details = fixture.nativeElement.querySelector('details') as HTMLDetailsElement;
    const summary = details.querySelector('summary') as HTMLElement;
    expect(details.open).toBe(true);
    expect(summary.tabIndex).toBeGreaterThanOrEqual(0);
    expect(fixture.nativeElement.textContent).toContain('Elapsed');
    expect(fixture.nativeElement.textContent).toContain('Update accepted');
  });

  it('announces the latest safe host trace event and guides legacy helpers', () => {
    fixture.componentRef.setInput('status', status({ protocolVersion: 2, trace: null }));
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[aria-live="polite"]')?.textContent).toContain(
      'Applying trusted update',
    );
    expect(fixture.nativeElement.textContent).toContain('refresh the Ubuntu installer bundle');
    expect(fixture.nativeElement.textContent).not.toMatch(/docker|sha256:|\/opt\/|exitCode/i);
  });
});

function status(overrides: Partial<SystemUpdateStatusDto>): SystemUpdateStatusDto {
  return {
    protocolVersion: 1,
    supported: true,
    channel: 'stable',
    currentVersion: 'v1.3.0',
    targetVersion: 'v1.4.0',
    phase: 'applying',
    progressStage: null,
    updateAvailable: false,
    canApply: false,
    reasonCode: 'update_applying',
    detail: 'ReachCommander is applying the trusted update.',
    operationId: 'operation-1',
    lastCheckedAt: '2026-08-25T10:00:00Z',
    updatedAt: new Date().toISOString(),
    trace: null,
    ...overrides,
  };
}
