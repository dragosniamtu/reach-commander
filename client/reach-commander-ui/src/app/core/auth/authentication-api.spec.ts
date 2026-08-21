import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { AuthenticationApi } from './authentication-api';

describe('AuthenticationApi', () => {
  let api: AuthenticationApi;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [AuthenticationApi, provideHttpClient(), provideHttpClientTesting()],
    });
    api = TestBed.inject(AuthenticationApi);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('uses the exact session and antiforgery routes', async () => {
    const session = api.getSession();
    const sessionRequest = http.expectOne('/api/auth/session');
    expect(sessionRequest.request.method).toBe('GET');
    sessionRequest.flush({ state: 'anonymous', username: null });
    await expect(session).resolves.toEqual({ state: 'anonymous', username: null });

    const antiforgery = api.getAntiforgeryToken();
    const antiforgeryRequest = http.expectOne('/api/auth/antiforgery');
    expect(antiforgeryRequest.request.method).toBe('GET');
    antiforgeryRequest.flush({ requestToken: 'csrf-token' });
    await expect(antiforgery).resolves.toBe('csrf-token');
  });

  it('passes request-only credentials to setup and login', async () => {
    const setupCommand = {
      setupCode: 'one-time-code',
      username: 'dragos',
      password: 'a-long-test-password',
    };
    const setup = api.setup(setupCommand);
    const setupRequest = http.expectOne('/api/auth/setup');
    expect(setupRequest.request.method).toBe('POST');
    expect(setupRequest.request.body).toEqual(setupCommand);
    setupRequest.flush({ state: 'authenticated', username: 'dragos' });
    await expect(setup).resolves.toEqual({ state: 'authenticated', username: 'dragos' });

    const loginCommand = { username: 'dragos', password: 'a-long-test-password' };
    const login = api.login(loginCommand);
    const loginRequest = http.expectOne('/api/auth/login');
    expect(loginRequest.request.method).toBe('POST');
    expect(loginRequest.request.body).toEqual(loginCommand);
    loginRequest.flush({ state: 'authenticated', username: 'dragos' });
    await expect(login).resolves.toEqual({ state: 'authenticated', username: 'dragos' });
  });

  it('uses the exact logout and password-change routes', async () => {
    const logout = api.logout();
    const logoutRequest = http.expectOne('/api/auth/logout');
    expect(logoutRequest.request.method).toBe('POST');
    expect(logoutRequest.request.body).toBeNull();
    logoutRequest.flush(null);
    await expect(logout).resolves.toBeNull();

    const command = {
      currentPassword: 'a-long-test-password',
      newPassword: 'a-different-test-password',
    };
    const change = api.changePassword(command);
    const changeRequest = http.expectOne('/api/auth/password');
    expect(changeRequest.request.method).toBe('POST');
    expect(changeRequest.request.body).toEqual(command);
    changeRequest.flush({ state: 'authenticated', username: 'dragos' });
    await expect(change).resolves.toEqual({ state: 'authenticated', username: 'dragos' });
  });
});
