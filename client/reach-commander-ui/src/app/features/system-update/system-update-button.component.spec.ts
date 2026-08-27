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

  it('shows the backend current version beside the update action', () => {
    fixture.componentRef.setInput('status', status({ currentVersion: 'v1.0.2' }));
    fixture.detectChanges();

    const trigger = fixture.nativeElement.querySelector(
      '[data-testid="system-update-trigger"]',
    ) as HTMLElement;
    const badge = fixture.nativeElement.querySelector(
      '[data-testid="current-version"]',
    ) as HTMLElement;

    expect(trigger.nextElementSibling).toBe(badge);
    expect(badge.textContent?.trim()).toBe('v1.0.2');
    expect(badge.getAttribute('aria-label')).toBe(
      'Current ReachCommander version v1.0.2',
    );
    expect(badge.title).toBe('Current ReachCommander version v1.0.2');
  });

  it('shows a compact loading version before update status arrives', () => {
    fixture.detectChanges();
    const badge = fixture.nativeElement.querySelector(
      '[data-testid="current-version"]',
    ) as HTMLElement;

    expect(badge.textContent?.trim()).toBe('v…');
    expect(badge.getAttribute('aria-label')).toBe(
      'Current ReachCommander version is loading',
    );
  });

  it('shows an unavailable version when status omits currentVersion', () => {
    fixture.componentRef.setInput('status', status({ currentVersion: null }));
    fixture.detectChanges();
    const badge = fixture.nativeElement.querySelector(
      '[data-testid="current-version"]',
    ) as HTMLElement;

    expect(badge.textContent?.trim()).toBe('Unknown');
    expect(badge.title).toBe('Current ReachCommander version is unavailable');
  });

  it('keeps a long edge version complete for assistive text and the tooltip', () => {
    const version = 'edge@0123456789abcdef';
    fixture.componentRef.setInput('status', status({ currentVersion: version }));
    fixture.detectChanges();
    const badge = fixture.nativeElement.querySelector(
      '[data-testid="current-version"]',
    ) as HTMLElement;

    expect(badge.textContent?.trim()).toBe(version);
    expect(badge.getAttribute('aria-label')).toContain(version);
    expect(badge.title).toContain(version);
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
    progressStage: null,
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
