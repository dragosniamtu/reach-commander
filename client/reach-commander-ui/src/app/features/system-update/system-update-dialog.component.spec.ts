import { ComponentFixture, TestBed } from '@angular/core/testing';
import { SystemUpdateStatusDto } from '../../core/api/api.models';
import { SystemUpdateDialogComponent } from './system-update-dialog.component';

describe('SystemUpdateDialogComponent', () => {
  let fixture: ComponentFixture<SystemUpdateDialogComponent>;
  let opener: HTMLButtonElement;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [SystemUpdateDialogComponent] }).compileComponents();
    fixture = TestBed.createComponent(SystemUpdateDialogComponent);
    opener = document.createElement('button');
    document.body.append(opener);
    fixture.componentRef.setInput('status', status());
    fixture.componentRef.setInput('opener', opener);
    fixture.detectChanges();
  });

  afterEach(() => opener.remove());

  it('shows captured versions and submits only after explicit confirmation', () => {
    const apply = vi.spyOn(fixture.componentInstance.apply, 'emit');

    const confirm = [...fixture.nativeElement.querySelectorAll('button')]
      .find((button: HTMLButtonElement) => button.textContent?.includes('Update ReachCommander'))!;
    confirm.click();

    expect(fixture.nativeElement.textContent).toContain('v1.3.0');
    expect(fixture.nativeElement.textContent).toContain('v1.4.0');
    expect(fixture.nativeElement.textContent).toContain('restart');
    expect(apply).toHaveBeenCalledOnce();
    expect(apply).toHaveBeenCalledWith();
  });

  it('closes on Cancel and Escape without submitting and restores opener focus', () => {
    const apply = vi.spyOn(fixture.componentInstance.apply, 'emit');
    const closed = vi.spyOn(fixture.componentInstance.closed, 'emit');
    const cancel = [...fixture.nativeElement.querySelectorAll('button')]
      .find((button: HTMLButtonElement) => button.textContent?.includes('Cancel'))!;

    cancel.click();
    expect(closed).toHaveBeenCalledOnce();
    expect(apply).not.toHaveBeenCalled();
    expect(document.activeElement).toBe(opener);

    closed.mockClear();
    fixture.nativeElement.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    expect(closed).toHaveBeenCalledOnce();
    expect(apply).not.toHaveBeenCalled();
  });
});

function status(): SystemUpdateStatusDto {
  return {
    protocolVersion: 1,
    supported: true,
    channel: 'stable',
    currentVersion: 'v1.3.0',
    targetVersion: 'v1.4.0',
    phase: 'available',
    updateAvailable: true,
    canApply: true,
    reasonCode: 'update_available',
    detail: 'A trusted ReachCommander update is available.',
    operationId: null,
    lastCheckedAt: '2026-08-25T10:00:00Z',
    updatedAt: '2026-08-25T10:00:00Z',
  };
}
