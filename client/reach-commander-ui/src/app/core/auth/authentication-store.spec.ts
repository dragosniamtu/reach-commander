import { HttpErrorResponse } from '@angular/common/http';
import { AuthenticationApi } from './authentication-api';
import { AuthenticationChannel } from './authentication-channel';
import {
  AuthenticationSessionDto,
  ChangePasswordCommand,
  LoginCommand,
  SetupCommand,
} from './authentication.models';
import { AuthenticationStore } from './authentication-store';

describe('AuthenticationStore', () => {
  let api: FakeAuthenticationApi;
  let channel: AuthenticationChannel;
  let store: AuthenticationStore;

  beforeEach(() => {
    api = new FakeAuthenticationApi();
    channel = new AuthenticationChannel();
    store = new AuthenticationStore(api as unknown as AuthenticationApi, channel);
  });

  it.each([
    [{ state: 'setupRequired', username: null }, 'setupRequired', null],
    [{ state: 'anonymous', username: null }, 'anonymous', null],
    [{ state: 'authenticated', username: 'dragos' }, 'authenticated', 'dragos'],
  ] as const)('maps server session %o to phase %s', async (session, phase, username) => {
    api.sessionResult = session;

    await store.initialize();

    expect(store.state().phase).toBe(phase);
    expect(store.state().username).toBe(username);
    expect(channel.token()).toBe('csrf-token-1');
    expect(JSON.stringify(store.state())).not.toContain('password');
  });

  it('moves to a generic unavailable state when initialization fails', async () => {
    api.antiforgeryHandler = () => Promise.reject(new Error('network detail must stay private'));

    await store.initialize();

    expect(store.state()).toEqual({
      phase: 'unavailable',
      username: null,
      pending: false,
      errorCode: 'request_failed',
      errorMessage: 'Authentication is temporarily unavailable.',
    });
    expect(JSON.stringify(store.state())).not.toContain('network detail');
  });

  it('fails closed when an authenticated session omits its username', async () => {
    api.sessionResult = { state: 'authenticated', username: null };

    await store.initialize();

    expect(store.state().phase).toBe('unavailable');
    expect(store.state().username).toBeNull();
  });

  it('coalesces duplicate initialization calls', async () => {
    const session = deferred<AuthenticationSessionDto>();
    api.sessionHandler = () => session.promise;

    const first = store.initialize();
    const second = store.initialize();
    await Promise.resolve();
    await Promise.resolve();
    expect(api.sessionRequests).toBe(1);

    session.resolve({ state: 'anonymous', username: null });
    await Promise.all([first, second]);
    expect(api.sessionRequests).toBe(1);
  });

  it('prevents duplicate login submits and refreshes the identity-bound token before exposure', async () => {
    api.sessionResult = { state: 'anonymous', username: null };
    await store.initialize();
    const login = deferred<AuthenticationSessionDto>();
    api.loginHandler = () => login.promise;
    const command = { username: 'dragos', password: 'a-long-test-password' };

    const first = store.login(command);
    const second = store.login(command);
    await Promise.resolve();
    expect(api.loginRequests).toBe(1);
    expect(store.state().pending).toBe(true);
    login.resolve({ state: 'authenticated', username: 'dragos' });
    await Promise.all([first, second]);

    expect(api.loginRequests).toBe(1);
    expect(api.antiforgeryRequests).toBe(2);
    expect(channel.token()).toBe('csrf-token-2');
    expect(store.state()).toEqual({
      phase: 'authenticated',
      username: 'dragos',
      pending: false,
      errorCode: null,
      errorMessage: null,
    });
    expect(JSON.stringify(store.state())).not.toContain(command.password);
  });

  it('submits setup credentials only to the API and refreshes the authenticated token', async () => {
    api.sessionResult = { state: 'setupRequired', username: null };
    await store.initialize();
    const command = {
      setupCode: 'one-time-code',
      username: 'dragos',
      password: 'a-long-test-password',
    };

    await store.setup(command);

    expect(api.setupRequests).toBe(1);
    expect(api.antiforgeryRequests).toBe(2);
    expect(store.state().phase).toBe('authenticated');
    expect(JSON.stringify(store.state())).not.toContain(command.setupCode);
    expect(JSON.stringify(store.state())).not.toContain(command.password);
  });

  it('keeps credential failures generic and never stores submitted commands', async () => {
    api.sessionResult = { state: 'anonymous', username: null };
    await store.initialize();
    api.loginHandler = () =>
      Promise.reject(
        new HttpErrorResponse({
          status: 401,
          error: { code: 'invalid_credentials', detail: 'server secret detail' },
        }),
      );
    const command = { username: 'dragos', password: 'a-long-test-password' };

    await store.login(command);

    expect(store.state()).toEqual({
      phase: 'anonymous',
      username: null,
      pending: false,
      errorCode: 'invalid_credentials',
      errorMessage: 'The supplied credentials are not valid.',
    });
    const serialized = JSON.stringify(store.state());
    expect(serialized).not.toContain(command.password);
    expect(serialized).not.toContain('server secret detail');
  });

  it('locks locally after logout even when the request fails', async () => {
    api.sessionResult = { state: 'authenticated', username: 'dragos' };
    await store.initialize();
    api.logoutHandler = () => Promise.reject(new Error('offline'));

    await store.logout();

    expect(api.logoutRequests).toBe(1);
    expect(channel.token()).toBeNull();
    expect(store.state()).toEqual({
      phase: 'anonymous',
      username: null,
      pending: false,
      errorCode: null,
      errorMessage: null,
    });
  });

  it('keeps the authenticated session on expected password validation failure', async () => {
    api.sessionResult = { state: 'authenticated', username: 'dragos' };
    await store.initialize();
    api.changePasswordHandler = () =>
      Promise.reject(
        new HttpErrorResponse({ status: 401, error: { code: 'invalid_credentials' } }),
      );
    const command = {
      currentPassword: 'a-long-test-password',
      newPassword: 'a-different-test-password',
    };

    await store.changePassword(command);

    expect(store.state().phase).toBe('authenticated');
    expect(store.state().username).toBe('dragos');
    expect(store.state().errorCode).toBe('invalid_credentials');
    expect(JSON.stringify(store.state())).not.toContain(command.currentPassword);
    expect(JSON.stringify(store.state())).not.toContain(command.newPassword);
  });

  it('keeps the current workspace authenticated after a successful password change', async () => {
    api.sessionResult = { state: 'authenticated', username: 'dragos' };
    await store.initialize();

    await store.changePassword({
      currentPassword: 'a-long-test-password',
      newPassword: 'a-different-test-password',
    });

    expect(api.changePasswordRequests).toBe(1);
    expect(api.antiforgeryRequests).toBe(2);
    expect(channel.token()).toBe('csrf-token-2');
    expect(store.state().phase).toBe('authenticated');
    expect(store.state().username).toBe('dragos');
  });

  it('locks immediately when the channel reports an unexpected unauthorized response', async () => {
    api.sessionResult = { state: 'authenticated', username: 'dragos' };
    await store.initialize();

    channel.notifyUnauthorized();

    expect(store.state().phase).toBe('anonymous');
    expect(store.state().username).toBeNull();
    expect(channel.token()).toBeNull();
  });
});

class FakeAuthenticationApi {
  sessionResult: AuthenticationSessionDto = { state: 'setupRequired', username: null };
  sessionRequests = 0;
  antiforgeryRequests = 0;
  setupRequests = 0;
  loginRequests = 0;
  logoutRequests = 0;
  changePasswordRequests = 0;
  sessionHandler: () => Promise<AuthenticationSessionDto> = () =>
    Promise.resolve(this.sessionResult);
  antiforgeryHandler: () => Promise<string> = () =>
    Promise.resolve(`csrf-token-${this.antiforgeryRequests}`);
  setupHandler: (command: SetupCommand) => Promise<AuthenticationSessionDto> = () =>
    Promise.resolve({ state: 'authenticated', username: 'dragos' });
  loginHandler: (command: LoginCommand) => Promise<AuthenticationSessionDto> = () =>
    Promise.resolve({ state: 'authenticated', username: 'dragos' });
  logoutHandler: () => Promise<void> = () => Promise.resolve();
  changePasswordHandler: (
    command: ChangePasswordCommand,
  ) => Promise<AuthenticationSessionDto> = () =>
    Promise.resolve({ state: 'authenticated', username: 'dragos' });

  getSession(): Promise<AuthenticationSessionDto> {
    this.sessionRequests++;
    return this.sessionHandler();
  }

  getAntiforgeryToken(): Promise<string> {
    this.antiforgeryRequests++;
    return this.antiforgeryHandler();
  }

  setup(command: SetupCommand): Promise<AuthenticationSessionDto> {
    this.setupRequests++;
    return this.setupHandler(command);
  }

  login(command: LoginCommand): Promise<AuthenticationSessionDto> {
    this.loginRequests++;
    return this.loginHandler(command);
  }

  logout(): Promise<void> {
    this.logoutRequests++;
    return this.logoutHandler();
  }

  changePassword(command: ChangePasswordCommand): Promise<AuthenticationSessionDto> {
    this.changePasswordRequests++;
    return this.changePasswordHandler(command);
  }
}

function deferred<T>(): {
  readonly promise: Promise<T>;
  readonly resolve: (value: T) => void;
} {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((accept) => {
    resolve = accept;
  });
  return { promise, resolve };
}
