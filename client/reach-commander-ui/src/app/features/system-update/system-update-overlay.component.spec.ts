import { ComponentFixture, TestBed } from '@angular/core/testing';
import { SystemUpdateStatusDto } from '../../core/api/api.models';
import { SystemUpdateOverlayComponent } from './system-update-overlay.component';

describe('SystemUpdateOverlayComponent', () => {
  let fixture: ComponentFixture<SystemUpdateOverlayComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [SystemUpdateOverlayComponent] }).compileComponents();
    fixture = TestBed.createComponent(SystemUpdateOverlayComponent);
  });

  it('keeps a blocking reconnect state while the backend restarts', () => {
    fixture.componentRef.setInput('status', status({ phase: 'applying' }));
    fixture.componentRef.setInput('reconnecting', true);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[aria-modal="true"]')).not.toBeNull();
    expect(fixture.nativeElement.textContent).toContain('Reconnecting');
    expect(fixture.nativeElement.querySelector('button')).toBeNull();
  });

  it.each([
    ['rolledBack', 'previous version was restored'],
    ['failed', 'reachcommander doctor'],
  ] as const)('shows dismissible %s guidance', (phase, text) => {
    const dismissed = vi.spyOn(fixture.componentInstance.dismissed, 'emit');
    fixture.componentRef.setInput('status', status({ phase }));
    fixture.componentRef.setInput('reconnecting', false);
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent.toLowerCase()).toContain(text);
    (fixture.nativeElement.querySelector('button') as HTMLButtonElement).click();
    expect(dismissed).toHaveBeenCalledOnce();
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
    updateAvailable: false,
    canApply: false,
    reasonCode: 'update_applying',
    detail: 'ReachCommander is applying the trusted update.',
    operationId: 'operation-1',
    lastCheckedAt: '2026-08-25T10:00:00Z',
    updatedAt: '2026-08-25T10:00:00Z',
    ...overrides,
  };
}
