import { TestBed } from '@angular/core/testing';
import { CommanderApiPort, SystemUpdateSupportBundleDownload } from '../../core/api/api.models';
import { ProtectedStateResetService } from '../../core/auth/protected-state-reset.service';
import {
  SYSTEM_UPDATE_SUPPORT_BUNDLE_SAVER,
  SystemUpdateSupportBundleService,
} from './system-update-support-bundle.service';

describe('SystemUpdateSupportBundleService', () => {
  let api: StubApi;
  let save: ReturnType<typeof vi.fn>;
  let service: SystemUpdateSupportBundleService;

  beforeEach(() => {
    api = new StubApi();
    save = vi.fn();
    TestBed.configureTestingModule({
      providers: [
        SystemUpdateSupportBundleService,
        { provide: CommanderApiPort, useValue: api as unknown as CommanderApiPort },
        { provide: SYSTEM_UPDATE_SUPPORT_BUNDLE_SAVER, useValue: { save } },
      ],
    });
    service = TestBed.inject(SystemUpdateSupportBundleService);
  });

  it('downloads one private bundle without storing its contents', async () => {
    const result = service.download();
    const duplicate = service.download();

    expect(service.pending()).toBe(true);
    await Promise.all([result, duplicate]);

    expect(save).toHaveBeenCalledOnce();
    expect(api.downloadCount).toBe(1);
    expect(save).toHaveBeenCalledWith(api.download);
    expect(service.pending()).toBe(false);
    expect(service.error()).toBeNull();
  });

  it('shows fixed CLI recovery guidance without exposing server errors', async () => {
    api.failure = new Error('token=secret /srv/private');

    await service.download();

    expect(save).not.toHaveBeenCalled();
    expect(service.error()).toContain('sudo reachcommander support-bundle');
    expect(service.error()).not.toContain('token=secret');

    TestBed.inject(ProtectedStateResetService).reset();
    expect(service.error()).toBeNull();
  });

  it('discards a pending bundle if its update overlay scope is destroyed', async () => {
    let resolveDownload!: (download: SystemUpdateSupportBundleDownload) => void;
    api.pendingDownload = new Promise((resolve) => {
      resolveDownload = resolve;
    });

    const result = service.download();
    TestBed.resetTestingModule();
    resolveDownload(api.download);
    await result;

    expect(save).not.toHaveBeenCalled();
  });
});

class StubApi {
  readonly download: SystemUpdateSupportBundleDownload = {
    blob: new Blob(['sanitized'], { type: 'application/zip' }),
    fileName: 'reachcommander-support-20260827T120000Z.zip',
  };
  failure: unknown = null;
  pendingDownload: Promise<SystemUpdateSupportBundleDownload> | null = null;
  downloadCount = 0;

  downloadSystemUpdateSupportBundle(): Promise<SystemUpdateSupportBundleDownload> {
    this.downloadCount++;
    if (this.failure !== null) {
      return Promise.reject(this.failure);
    }
    return this.pendingDownload ?? Promise.resolve(this.download);
  }
}
