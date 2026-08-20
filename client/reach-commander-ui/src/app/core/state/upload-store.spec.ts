import { HttpErrorResponse } from '@angular/common/http';
import { Observable, Subject } from 'rxjs';
import {
  CommanderApiPort,
  FileEntryDto,
  SourceDto,
  SystemMetricsDto,
  UploadEvent,
  UploadLimitsDto,
  UploadResultDto,
  UploadTarget,
} from '../api/api.models';
import { UploadContext } from './upload.models';
import { UploadStore } from './upload-store';

describe('UploadStore', () => {
  let api: FakeUploadApi;
  let store: UploadStore;

  beforeEach(() => {
    api = new FakeUploadApi();
    store = new UploadStore(api);
  });

  it('captures an immutable destination and file-list copy while caching deployment limits', async () => {
    const context = uploadContext();
    const files = [file('one.txt', 3), file('two.txt', 2)];
    const completed = vi.fn();

    store.open(context, files, completed);
    files.push(file('late.txt', 1));
    await settlePromises();

    expect(store.state().phase).toBe('review');
    expect(store.state().context).toEqual(context);
    expect(store.state().context).not.toBe(context);
    expect(store.state().files.map((candidate) => candidate.name)).toEqual(['one.txt', 'two.txt']);
    expect(store.state().limits).toEqual(api.limits);
    expect(api.limitRequests).toBe(1);

    expect(store.close()).toBe(true);
    store.open(context, [file('again.txt', 1)], completed);
    await settlePromises();
    expect(api.limitRequests).toBe(1);
  });

  it('recalculates totals and reports count, per-file, and batch preflight failures', async () => {
    api.limits = { maxFileBytes: 5, maxBatchBytes: 6, maxFilesPerBatch: 2 };
    store.open(
      uploadContext(),
      [file('large.bin', 6), file('one.bin', 1), file('two.bin', 1)],
      vi.fn(),
    );
    await settlePromises();

    expect(store.state().totalBytes).toBe(8);
    expect(
      store
        .state()
        .preflightIssues.map((issue) => issue.code)
        .sort(),
    ).toEqual(['upload_batch_too_large', 'upload_file_too_large', 'upload_too_many_files']);
    expect(store.start()).toBe(false);
    expect(api.uploadRequests).toHaveLength(0);

    store.removeFile(2);
    expect(store.state().totalBytes).toBe(7);
    expect(
      store
        .state()
        .preflightIssues.map((issue) => issue.code)
        .sort(),
    ).toEqual(['upload_batch_too_large', 'upload_file_too_large']);
    store.removeFile(0);
    expect(store.state().totalBytes).toBe(1);
    expect(store.state().preflightIssues).toEqual([]);
  });

  it('moves through upload progress and finalization, then refreshes exactly once', async () => {
    const completed = vi.fn();
    store.open(uploadContext(), [file('one.txt', 3)], completed);
    await settlePromises();

    expect(store.start()).toBe(true);
    const request = api.uploadRequests[0]!;
    expect(store.state().phase).toBe('uploading');

    request.events.next({ kind: 'progress', loadedBytes: 2, totalBytes: 3 });
    expect(store.state().progressLoadedBytes).toBe(2);
    expect(store.state().phase).toBe('uploading');

    request.events.next({ kind: 'progress', loadedBytes: 3, totalBytes: 3 });
    expect(store.state().phase).toBe('finalizing');
    request.events.next({ kind: 'completed', result: uploadResult() });
    request.events.next({ kind: 'completed', result: uploadResult() });

    expect(store.state().phase).toBe('completed');
    expect(store.state().result).toEqual(uploadResult());
    expect(completed).toHaveBeenCalledTimes(1);
  });

  it('cancels the active request and ignores late events from the cancelled token', async () => {
    const completed = vi.fn();
    store.open(uploadContext(), [file('one.txt', 3)], completed);
    await settlePromises();
    store.start();
    const request = api.uploadRequests[0]!;

    expect(store.cancel()).toBe(true);
    request.events.next({ kind: 'completed', result: uploadResult() });

    expect(request.cancelled).toBe(1);
    expect(store.state().phase).toBe('cancelled');
    expect(store.state().result).toBeNull();
    expect(completed).not.toHaveBeenCalled();
  });

  it('ignores events from a previous request after opening a new review', async () => {
    store.open(uploadContext(), [file('old.txt', 2)], vi.fn());
    await settlePromises();
    store.start();
    const oldRequest = api.uploadRequests[0]!;
    store.cancel();
    store.open(uploadContext({ directoryPath: '/New' }), [file('new.txt', 4)], vi.fn());
    store.start();
    const currentRequest = api.uploadRequests[1]!;

    oldRequest.events.next({ kind: 'progress', loadedBytes: 2, totalBytes: 2 });
    currentRequest.events.next({ kind: 'progress', loadedBytes: 1, totalBytes: 4 });

    expect(store.state().context?.directoryPath).toBe('/New');
    expect(store.state().progressLoadedBytes).toBe(1);
    expect(store.state().phase).toBe('uploading');
  });

  it('maps safe Problem Details codes without displaying server detail and retains review files', async () => {
    store.open(uploadContext(), [file('existing.txt', 3)], vi.fn());
    await settlePromises();
    store.start();
    api.uploadRequests[0]!.events.error(
      new HttpErrorResponse({
        status: 409,
        error: {
          code: 'upload_name_conflict',
          detail: 'D:\\private\\existing.txt',
        },
      }),
    );

    expect(store.state().phase).toBe('failed');
    expect(store.state().errorCode).toBe('upload_name_conflict');
    expect(store.state().errorMessage).toBe('One or more files already exist in this folder.');
    expect(store.state().errorMessage).not.toContain('private');
    expect(store.state().files.map((candidate) => candidate.name)).toEqual(['existing.txt']);
  });

  it('retries a failed limits request the next time a review opens', async () => {
    api.limitHandler = () => Promise.reject(new Error('offline'));
    store.open(uploadContext(), [file('one.txt', 1)], vi.fn());
    await settlePromises();

    expect(store.state().limits).toBeNull();
    expect(store.state().errorCode).toBe('upload_limits_unavailable');
    expect(store.state().errorMessage).toBe('Upload limits could not be loaded.');
    expect(store.close()).toBe(true);

    api.limitHandler = () => Promise.resolve(api.limits);
    store.open(uploadContext(), [file('two.txt', 1)], vi.fn());
    await settlePromises();

    expect(store.state().limits).toEqual(api.limits);
    expect(api.limitRequests).toBe(2);
  });

  it('refuses to close during finalization and otherwise clears browser File references', async () => {
    store.open(uploadContext(), [file('one.txt', 3)], vi.fn());
    await settlePromises();
    store.start();
    api.uploadRequests[0]!.events.next({
      kind: 'progress',
      loadedBytes: 3,
      totalBytes: 3,
    });

    expect(store.close()).toBe(false);
    expect(store.state().files).toHaveLength(1);

    api.uploadRequests[0]!.events.next({ kind: 'completed', result: uploadResult() });
    expect(store.close()).toBe(true);
    expect(store.state().phase).toBe('closed');
    expect(store.state().context).toBeNull();
    expect(store.state().files).toEqual([]);
  });
});

class FakeUploadApi extends CommanderApiPort {
  limits: UploadLimitsDto = {
    maxFileBytes: 10,
    maxBatchBytes: 20,
    maxFilesPerBatch: 3,
  };
  limitRequests = 0;
  limitHandler: () => Promise<UploadLimitsDto> = () => Promise.resolve(this.limits);
  readonly uploadRequests: UploadRequest[] = [];

  getUploadLimits(): Promise<UploadLimitsDto> {
    this.limitRequests++;
    return this.limitHandler();
  }

  uploadFiles(target: UploadTarget, files: readonly File[]): Observable<UploadEvent> {
    const events = new Subject<UploadEvent>();
    const request: UploadRequest = {
      target,
      files: [...files],
      events,
      cancelled: 0,
    };
    this.uploadRequests.push(request);
    return new Observable((subscriber) => {
      const subscription = events.subscribe(subscriber);
      return () => {
        request.cancelled++;
        subscription.unsubscribe();
      };
    });
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

  async getSystemMetrics(): Promise<SystemMetricsDto> {
    throw new Error('Not used');
  }

  async getSources(): Promise<readonly SourceDto[]> {
    return [];
  }

  async listFiles(): Promise<readonly FileEntryDto[]> {
    return [];
  }

  async getInfo(): Promise<FileEntryDto> {
    throw new Error('Not used');
  }
}

interface UploadRequest {
  readonly target: UploadTarget;
  readonly files: readonly File[];
  readonly events: Subject<UploadEvent>;
  cancelled: number;
}

function uploadContext(overrides: Partial<UploadContext> = {}): UploadContext {
  return {
    side: 'left',
    sourceId: 'media',
    sourceName: 'Media',
    directoryPath: '/Movies',
    ...overrides,
  };
}

function file(name: string, size: number): File {
  return new File([new Uint8Array(size)], name);
}

function uploadResult(): UploadResultDto {
  return {
    uploadedCount: 1,
    totalBytes: 3,
    files: [{ name: 'one.txt', relativePath: '/Movies/one.txt', size: 3 }],
  };
}

async function settlePromises(): Promise<void> {
  await Promise.resolve();
  await Promise.resolve();
}
