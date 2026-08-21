import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { AuthenticationStore } from '../../core/auth/authentication-store';
import { AccountMenuComponent } from './account-menu.component';

describe('AccountMenuComponent', () => {
  let fixture: ComponentFixture<AccountMenuComponent>;
  const authentication = {
    state: signal({
      phase: 'authenticated' as const,
      username: 'dragos',
      pending: false,
      errorCode: null as string | null,
      errorMessage: null as string | null,
    }),
    logout: vi.fn(() => Promise.resolve()),
    changePassword: vi.fn(() => Promise.resolve()),
  };

  beforeEach(async () => {
    vi.clearAllMocks();
    authentication.state.set({
      phase: 'authenticated',
      username: 'dragos',
      pending: false,
      errorCode: null,
      errorMessage: null,
    });
    await TestBed.configureTestingModule({
      imports: [AccountMenuComponent],
      providers: [{ provide: AuthenticationStore, useValue: authentication }],
    }).compileComponents();
    fixture = TestBed.createComponent(AccountMenuComponent);
    fixture.detectChanges();
  });

  it('shows the authenticated username and exposes account actions', () => {
    const trigger = element<HTMLButtonElement>('[data-testid="account-menu-trigger"]');
    expect(trigger.textContent).toContain('dragos');
    expect(trigger.getAttribute('aria-expanded')).toBe('false');

    trigger.click();
    fixture.detectChanges();

    expect(trigger.getAttribute('aria-expanded')).toBe('true');
    expect(element('[data-testid="account-menu"]')).not.toBeNull();
  });

  it('validates password confirmation locally without submitting secrets', async () => {
    openPasswordDialog();
    fixture.componentInstance.passwordForm.setValue({
      currentPassword: 'a-long-current-password',
      newPassword: 'a-long-new-password',
      confirmPassword: 'a-different-password',
    });

    await fixture.componentInstance.submitPasswordChange();
    fixture.detectChanges();

    expect(authentication.changePassword).not.toHaveBeenCalled();
    expect(fixture.nativeElement.textContent).toContain('New passwords do not match.');
  });

  it('clears the form, closes, announces success, and restores trigger focus', async () => {
    const trigger = element<HTMLButtonElement>('[data-testid="account-menu-trigger"]');
    trigger.focus();
    openPasswordDialog();
    fixture.componentInstance.passwordForm.setValue({
      currentPassword: 'a-long-current-password',
      newPassword: 'a-long-new-password',
      confirmPassword: 'a-long-new-password',
    });

    await fixture.componentInstance.submitPasswordChange();
    fixture.detectChanges();
    await Promise.resolve();

    expect(authentication.changePassword).toHaveBeenCalledWith({
      currentPassword: 'a-long-current-password',
      newPassword: 'a-long-new-password',
    });
    expect(fixture.componentInstance.passwordForm.getRawValue()).toEqual({
      currentPassword: '',
      newPassword: '',
      confirmPassword: '',
    });
    expect(fixture.componentInstance.passwordDialogOpen()).toBe(false);
    expect(fixture.nativeElement.textContent).toContain(
      'Password changed. Other sessions were signed out.',
    );
    expect(document.activeElement).toBe(trigger);
  });

  it('cannot close the password dialog while a change is pending', () => {
    openPasswordDialog();
    authentication.state.update((state) => ({ ...state, pending: true }));
    fixture.detectChanges();

    fixture.componentInstance.closePasswordDialog();

    expect(fixture.componentInstance.passwordDialogOpen()).toBe(true);
  });

  it('logs out through the authentication store', () => {
    element<HTMLButtonElement>('[data-testid="account-menu-trigger"]').click();
    fixture.detectChanges();
    element<HTMLButtonElement>('[data-testid="logout"]').click();

    expect(authentication.logout).toHaveBeenCalledOnce();
  });

  function openPasswordDialog(): void {
    element<HTMLButtonElement>('[data-testid="account-menu-trigger"]').click();
    fixture.detectChanges();
    element<HTMLButtonElement>('[data-testid="change-password"]').click();
    fixture.detectChanges();
    expect(element('[data-testid="change-password-dialog"]')).not.toBeNull();
  }

  function element<T extends Element = Element>(selector: string): T {
    return fixture.nativeElement.querySelector(selector) as T;
  }
});
