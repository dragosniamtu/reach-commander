import { ComponentFixture, TestBed } from '@angular/core/testing';
import { SystemUpdateStatusDto } from '../../core/api/api.models';
import { SystemUpdateButtonComponent } from './system-update-button.component';

describe('SystemUpdateButtonComponent', () => {
  let fixture: ComponentFixture<SystemUpdateButtonComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [SystemUpdateButtonComponent] }).compileComponents();
    fixture = TestBed.createComponent(SystemUpdateButtonComponent);
  });

  it('enables a verified available update and announces its target', () => {
    fixture.componentRef.setInput('status', status({
      phase: 'available',
      targetVersion: 'v1.4.0',
      updateAvailable: true,
      canApply: true,
    }));
    fixture.detectChanges();
    const button = fixture.nativeElement.querySelector('button') as HTMLButtonElement;

    expect(button.disabled).toBe(false);
    expect(button.getAttribute('aria-label')).toBe('Update available: v1.4.0');
    expect(fixture.nativeElement.querySelector('.availability-dot')).not.toBeNull();
  });

  it.each([
    ['checking', 'Checking for updates'],
    ['current', 'ReachCommander is up to date'],
    ['blocked', 'Update waiting for operations to finish'],
    ['applying', 'Updating ReachCommander'],
    ['completed', 'ReachCommander update completed'],
    ['rolledBack', 'Previous version restored after update failure'],
    ['failed', 'Update requires administrator attention'],
    ['unavailable', 'System updates unavailable: System updates are unavailable.'],
  ] as const)('announces %s state', (phase, label) => {
    fixture.componentRef.setInput('status', status({
      phase,
      supported: phase !== 'unavailable',
      detail: phase === 'unavailable' ? 'System updates are unavailable.' : 'Detail.',
    }));
    fixture.detectChanges();

    expect(
      fixture.nativeElement.querySelector('button').getAttribute('aria-label'),
    ).toBe(label);
  });

  it('keeps a disabled pinned control focusable through its descriptive wrapper', () => {
    fixture.componentRef.setInput('status', status({
      phase: 'current',
      channel: 'v1.3.0',
      reasonCode: 'version_pinned',
      detail: 'Updates are disabled while this deployment is version-pinned.',
    }));
    fixture.detectChanges();
    const wrapper = fixture.nativeElement.querySelector('.update-control') as HTMLElement;

    expect(wrapper.tabIndex).toBe(0);
    expect(wrapper.title).toContain('Updates disabled while version-pinned');
    expect(wrapper.title).toContain('Channel: v1.3.0');
  });
});

function status(overrides: Partial<SystemUpdateStatusDto>): SystemUpdateStatusDto {
  return {
    protocolVersion: 1,
    supported: true,
    channel: 'stable',
    currentVersion: 'v1.3.0',
    targetVersion: null,
    phase: 'current',
    updateAvailable: false,
    canApply: false,
    reasonCode: 'up_to_date',
    detail: 'ReachCommander is up to date.',
    operationId: null,
    lastCheckedAt: '2026-08-25T10:00:00Z',
    updatedAt: '2026-08-25T10:00:00Z',
    ...overrides,
  };
}
