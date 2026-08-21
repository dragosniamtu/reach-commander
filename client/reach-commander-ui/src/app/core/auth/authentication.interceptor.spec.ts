import { HttpClient, HttpErrorResponse, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { AuthenticationChannel } from './authentication-channel';
import { authenticationInterceptor } from './authentication.interceptor';

describe('authenticationInterceptor', () => {
  let client: HttpClient;
  let http: HttpTestingController;
  let channel: AuthenticationChannel;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        AuthenticationChannel,
        provideHttpClient(withInterceptors([authenticationInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    client = TestBed.inject(HttpClient);
    http = TestBed.inject(HttpTestingController);
    channel = TestBed.inject(AuthenticationChannel);
  });

  afterEach(() => http.verify());

  it('adds the in-memory antiforgery token only to unsafe same-origin API requests', () => {
    channel.setAntiforgeryToken('csrf-token');
    client.post('/api/auth/login', {}).subscribe();
    const post = http.expectOne('/api/auth/login');
    expect(post.request.withCredentials).toBe(true);
    expect(post.request.headers.get('X-ReachCommander-CSRF')).toBe('csrf-token');
    post.flush({});

    client.get('/api/auth/session').subscribe();
    const get = http.expectOne('/api/auth/session');
    expect(get.request.withCredentials).toBe(true);
    expect(get.request.headers.has('X-ReachCommander-CSRF')).toBe(false);
    get.flush({});
  });

  it('leaves cross-origin requests untouched', () => {
    channel.setAntiforgeryToken('csrf-token');
    client.post('https://example.com/api/data', {}).subscribe();
    const request = http.expectOne('https://example.com/api/data');

    expect(request.request.withCredentials).toBe(false);
    expect(request.request.headers.has('X-ReachCommander-CSRF')).toBe(false);
    request.flush({});
  });

  it('notifies on stale-cookie 401 and rethrows the original response', () => {
    const notified = vi.fn();
    channel.unauthorized$.subscribe(notified);
    const received: HttpErrorResponse[] = [];
    client.get('/api/sources').subscribe({
      error: (error: HttpErrorResponse) => received.push(error),
    });
    const request = http.expectOne('/api/sources');
    request.flush(
      { code: 'authentication_state_unavailable' },
      { status: 401, statusText: 'Unauthorized' },
    );

    expect(notified).toHaveBeenCalledOnce();
    expect(received[0]).toBeInstanceOf(HttpErrorResponse);
    expect(received[0]?.status).toBe(401);
  });

  it.each([
    ['/api/auth/login', 'invalid_credentials'],
    ['/api/auth/setup', 'setup_failed'],
    ['/api/auth/password', 'invalid_credentials'],
  ])('does not lock for expected credential error from %s', (url, code) => {
    const notified = vi.fn();
    channel.unauthorized$.subscribe(notified);
    client.post(url, {}).subscribe({ error: () => undefined });
    http.expectOne(url).flush({ code }, { status: 401, statusText: 'Unauthorized' });

    expect(notified).not.toHaveBeenCalled();
  });
});
