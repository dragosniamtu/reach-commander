import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { AuthenticationStore } from '../../core/auth/authentication-store';
import {
  AuthenticationViewState,
  ChangePasswordCommand,
  LoginCommand,
  SetupCommand,
} from '../../core/auth/authentication.models';
import { AuthenticationScreenComponent } from './authentication-screen.component';

describe('AuthenticationScreenComponent', () => {
  let fixture: ComponentFixture<AuthenticationScreenComponent>;
  let auth: FakeAuthenticationStore;

  beforeEach(async () => {
    auth = new FakeAuthenticationStore();
    await TestBed.configureTestingModule({
      imports: [AuthenticationScreenComponent],
      providers: [{ provide: AuthenticationStore, useValue: auth }],
    }).compileComponents();
    fixture = TestBed.createComponent(AuthenticationScreenComponent);
  });

  it('renders accessible first-run fields and exact autocomplete hints', () => {
    auth.setState(state({ phase: 'setupRequired' }));
    fixture.detectChanges();

    expect(text()).toContain('Create administrator');
    expect(input('setup-code').autocomplete).toBe('one-time-code');
    expect(input('setup-username').autocomplete).toBe('username');
    expect(input('setup-password').autocomplete).toBe('new-password');
    expect(input('setup-password-confirmation').autocomplete).toBe('new-password');
    expect(input('setup-password').type).toBe('password');
    expect(fixture.nativeElement.querySelector('[aria-live="polite"]')).not.toBeNull();
  });

  it('renders login fields without revealing whether a username exists', () => {
    auth.setState(state({ phase: 'anonymous' }));
    fixture.detectChanges();

    expect(text()).toContain('Sign in');
    expect(input('login-username').autocomplete).toBe('username');
    expect(input('login-password').autocomplete).toBe('current-password');
    expect(text()).not.toContain('account exists');
  });

  it('keeps mismatched setup passwords client-side', async () => {
    auth.setState(state({ phase: 'setupRequired' }));
    fixture.detectChanges();
    setInput('setup-code', 'one-time-code');
    setInput('setup-username', 'dragos');
    setInput('setup-password', 'a-long-test-password');
    setInput('setup-password-confirmation', 'a-different-test-password');

    form('setup-form').requestSubmit();
    fixture.detectChanges();
    await fixture.whenStable();

    expect(auth.setup).not.toHaveBeenCalled();
    expect(text()).toContain('Passwords must match.');
  });

  it('submits login from the form keyboard path and clears the password after success', async () => {
    auth.setState(state({ phase: 'anonymous' }));
    auth.login.mockImplementation(async () => {
      auth.setState(state({ phase: 'authenticated', username: 'dragos' }));
    });
    fixture.detectChanges();
    setInput('login-username', 'dragos');
    setInput('login-password', 'a-long-test-password');

    form('login-form').requestSubmit();
    await fixture.whenStable();

    expect(auth.login).toHaveBeenCalledWith({
      username: 'dragos',
      password: 'a-long-test-password',
    });
    expect(fixture.componentInstance.loginForm.controls.password.value).toBe('');
  });

  it('clears setup code and passwords after successful account creation', async () => {
    auth.setState(state({ phase: 'setupRequired' }));
    auth.setup.mockImplementation(async () => {
      auth.setState(state({ phase: 'authenticated', username: 'dragos' }));
    });
    fixture.detectChanges();
    setInput('setup-code', 'one-time-code');
    setInput('setup-username', 'dragos');
    setInput('setup-password', 'a-long-test-password');
    setInput('setup-password-confirmation', 'a-long-test-password');

    form('setup-form').requestSubmit();
    await fixture.whenStable();

    expect(auth.setup).toHaveBeenCalledOnce();
    expect(fixture.componentInstance.setupForm.getRawValue()).toEqual({
      setupCode: '',
      username: '',
      password: '',
      passwordConfirmation: '',
    });
  });

  it('provides labeled password visibility controls that start hidden', () => {
    auth.setState(state({ phase: 'anonymous' }));
    fixture.detectChanges();
    const toggle = fixture.nativeElement.querySelector(
      '[data-testid="toggle-login-password"]',
    ) as HTMLButtonElement;

    expect(toggle.getAttribute('aria-label')).toBe('Show password');
    toggle.click();
    fixture.detectChanges();

    expect(input('login-password').type).toBe('text');
    expect(toggle.getAttribute('aria-label')).toBe('Hide password');
  });

  it('shows a retry action for unavailable authentication', () => {
    auth.setState(state({ phase: 'unavailable' }));
    fixture.detectChanges();

    expect(text()).toContain('Connection required');
    const retry = fixture.nativeElement.querySelector('[data-testid="auth-retry"]') as HTMLButtonElement;
    expect(retry.textContent).toContain('Retry');
    retry.click();
    expect(auth.retry).toHaveBeenCalledOnce();
  });

  function input(testId: string): HTMLInputElement {
    return fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
  }

  function setInput(testId: string, value: string): void {
    const element = input(testId);
    element.value = value;
    element.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  function form(testId: string): HTMLFormElement {
    return fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
  }

  function text(): string {
    return fixture.nativeElement.textContent.replace(/\s+/g, ' ');
  }
});

class FakeAuthenticationStore {
  private readonly mutableState = signal(state({ phase: 'checking' }));
  readonly state = this.mutableState.asReadonly();
  readonly retry = vi.fn(async (): Promise<void> => undefined);
  readonly setup = vi.fn(async (_command: SetupCommand): Promise<void> => undefined);
  readonly login = vi.fn(async (_command: LoginCommand): Promise<void> => undefined);
  readonly changePassword = vi.fn(
    async (_command: ChangePasswordCommand): Promise<void> => undefined,
  );
  readonly logout = vi.fn(async (): Promise<void> => undefined);
  readonly initialize = vi.fn(async (): Promise<void> => undefined);
  readonly lock = vi.fn();

  setState(value: AuthenticationViewState): void {
    this.mutableState.set(value);
  }
}

function state(overrides: Partial<AuthenticationViewState>): AuthenticationViewState {
  return {
    phase: 'checking',
    username: null,
    pending: false,
    errorCode: null,
    errorMessage: null,
    ...overrides,
  };
}
