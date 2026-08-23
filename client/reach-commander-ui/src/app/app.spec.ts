import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideServiceWorker } from '@angular/service-worker';
import { EMPTY, Observable } from 'rxjs';
import { App } from './app';
import { AuthenticationStore } from './core/auth/authentication-store';
import { AuthenticationViewState } from './core/auth/authentication.models';
import {
  CommanderApiPort,
  FileEntryDto,
  SourceDto,
  SystemMetricsDto,
  UploadEvent,
  UploadLimitsDto,
} from './core/api/api.models';

describe('App', () => {
  let api: AppTestApi;
  let auth: AppTestAuthenticationStore;

  beforeEach(async () => {
    localStorage.clear();
    document.documentElement.removeAttribute('data-theme');
    api = new AppTestApi();
    auth = new AppTestAuthenticationStore();
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [
        provideServiceWorker('ngsw-worker.js', { enabled: false }),
        { provide: CommanderApiPort, useValue: api },
        { provide: AuthenticationStore, useValue: auth },
      ],
    }).compileComponents();
  });

  afterEach(() => document.documentElement.removeAttribute('data-theme'));

  it('should create the app', () => {
    const fixture = TestBed.createComponent(App);
    const app = fixture.componentInstance;
    expect(app).toBeTruthy();
  });

  it('renders the ReachCommander dual-pane shell', async () => {
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    await fixture.whenStable();
    const compiled = fixture.nativeElement as HTMLElement;
    expect(compiled.textContent).toContain('ReachCommander');
    expect(compiled.querySelectorAll('app-commander-panel')).toHaveLength(2);
    expect(api.getSources).toHaveBeenCalled();
  });

  it('initializes a saved theme before rendering an unauthenticated screen', async () => {
    localStorage.setItem('reachcommander.theme.v1', 'norton');
    auth.setState(authState({ phase: 'anonymous' }));

    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    await fixture.whenStable();

    expect(document.documentElement.dataset['theme']).toBe('norton');
    expect(fixture.nativeElement.querySelector('app-authentication-screen')).not.toBeNull();
  });

  it.each(['checking', 'setupRequired', 'anonymous', 'unavailable'] as const)(
    'does not construct the commander while authentication is %s',
    async (phase) => {
      auth.setState(authState({ phase }));
      const fixture = TestBed.createComponent(App);
      fixture.detectChanges();
      await fixture.whenStable();

      const compiled = fixture.nativeElement as HTMLElement;
      expect(compiled.querySelector('app-commander-shell')).toBeNull();
      expect(compiled.querySelector('app-authentication-screen')).not.toBeNull();
      expect(api.getSources).not.toHaveBeenCalled();
      expect(auth.initialize).toHaveBeenCalledOnce();
    },
  );
});

class AppTestAuthenticationStore {
  private readonly mutableState = signal(
    authState({ phase: 'authenticated', username: 'integration-test' }),
  );
  readonly state = this.mutableState.asReadonly();
  readonly initialize = vi.fn(async (): Promise<void> => undefined);

  setState(value: AuthenticationViewState): void {
    this.mutableState.set(value);
  }
}

function authState(overrides: Partial<AuthenticationViewState>): AuthenticationViewState {
  return {
    phase: 'checking',
    username: null,
    pending: false,
    errorCode: null,
    errorMessage: null,
    ...overrides,
  };
}

class AppTestApi extends CommanderApiPort {
  async listArchive(): Promise<never> {
    throw new Error('Not used by this test.');
  }
  async getSystemMetrics(): Promise<SystemMetricsDto> {
    return {
      sampledAt: new Date().toISOString(),
      state: 'disabled',
      hostUptimeSeconds: null,
      cpu: null,
      memory: null,
      storage: [],
      gpus: [],
      fans: [],
      network: null,
      collectors: [],
    };
  }

  readonly getSources = vi.fn(async (): Promise<readonly SourceDto[]> => {
    return [
      {
        id: 'downloads',
        name: 'Downloads',
        isAvailable: true,
        isReadOnly: false,
        totalBytes: 100,
        usedBytes: 25,
        freeBytes: 75,
        defaultLeft: true,
        defaultRight: true,
      },
    ];
  });

  async listFiles(): Promise<readonly FileEntryDto[]> {
    return [];
  }

  async getInfo(): Promise<FileEntryDto> {
    throw new Error('Not used');
  }

  async getUploadLimits(): Promise<UploadLimitsDto> {
    return { maxFileBytes: 10, maxBatchBytes: 20, maxFilesPerBatch: 2 };
  }

  uploadFiles(): Observable<UploadEvent> {
    return EMPTY;
  }

  async previewBatchRename(): Promise<never> {
    throw new Error('Not used by this test');
  }

  async executeBatchRename(): Promise<never> {
    throw new Error('Not used by this test');
  }

  async undoBatchRename(): Promise<never> {
    throw new Error('Not used by this test');
  }

  async previewArchiveExtraction(): Promise<never> { throw new Error('Not used by this test'); }
  async executeArchiveExtraction(): Promise<never> { throw new Error('Not used by this test'); }
  async getArchiveExtraction(): Promise<never> { throw new Error('Not used by this test'); }
  async cancelArchiveExtraction(): Promise<never> { throw new Error('Not used by this test'); }
}
