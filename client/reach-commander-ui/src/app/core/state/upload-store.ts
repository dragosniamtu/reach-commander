import { HttpErrorResponse } from '@angular/common/http';
import { computed, Injectable, signal } from '@angular/core';
import { Subscription } from 'rxjs';
import { CommanderApiPort, UploadEvent, UploadLimitsDto } from '../api/api.models';
import { UploadContext, UploadPreflightIssue, UploadState } from './upload.models';

const uploadErrorMessages: Readonly<Record<string, string>> = {
  upload_name_invalid: 'One or more filenames are not valid on the destination.',
  upload_empty: 'Choose at least one file to upload.',
  upload_malformed: 'The upload request could not be read.',
  source_read_only: 'This source is read-only.',
  source_not_found: 'The destination source no longer exists.',
  source_unavailable: 'The destination source is unavailable.',
  path_forbidden: 'The destination path is not allowed.',
  entry_not_found: 'The destination folder no longer exists.',
  upload_name_conflict: 'One or more files already exist in this folder.',
  upload_file_too_large: 'One or more files exceed the upload limit.',
  upload_batch_too_large: 'The selected files exceed the batch upload limit.',
  upload_too_many_files: 'Too many files are selected for one upload.',
  upload_unsupported_media_type: 'The browser could not create a supported upload request.',
  upload_storage_unavailable: 'The destination storage is unavailable.',
  upload_cleanup_required:
    'The upload could not be safely completed. Ask an administrator to inspect the destination.',
};

@Injectable({ providedIn: 'root' })
export class UploadStore {
  private readonly mutableState = signal<UploadState>(closedState());
  private limitsCache: UploadLimitsDto | null = null;
  private nextRequestToken = 0;
  private activeSubscription: Subscription | null = null;
  private completionCallback: (() => void) | null = null;

  readonly state = this.mutableState.asReadonly();
  readonly isPending = computed(() => {
    const phase = this.state().phase;
    return phase === 'uploading' || phase === 'finalizing';
  });

  constructor(private readonly api: CommanderApiPort) {}

  open(context: UploadContext, files: readonly File[], onCompleted: () => void): void {
    if (this.state().phase === 'finalizing') {
      return;
    }

    this.releaseActiveRequest();
    const requestToken = ++this.nextRequestToken;
    const capturedContext = Object.freeze({ ...context });
    const capturedFiles = Object.freeze([...files]);
    this.completionCallback = onCompleted;
    this.mutableState.set(
      reviewState(capturedContext, capturedFiles, this.limitsCache, requestToken),
    );

    if (this.limitsCache === null) {
      void this.loadLimits(requestToken);
    }
  }

  removeFile(index: number): void {
    const state = this.state();
    if (
      !['review', 'failed', 'cancelled'].includes(state.phase) ||
      index < 0 ||
      index >= state.files.length
    ) {
      return;
    }

    const files = Object.freeze(
      state.files.filter((_, candidateIndex) => candidateIndex !== index),
    );
    this.mutableState.set(
      withFiles({
        ...state,
        phase: 'review',
        files,
        result: null,
        errorCode: null,
        errorMessage: null,
      }),
    );
  }

  start(): boolean {
    const state = this.state();
    if (
      !['review', 'failed', 'cancelled'].includes(state.phase) ||
      state.limitsPending ||
      state.limits === null ||
      state.preflightIssues.length > 0 ||
      state.context === null
    ) {
      return false;
    }

    this.releaseActiveRequest();
    const requestToken = ++this.nextRequestToken;
    this.mutableState.set({
      ...state,
      phase: 'uploading',
      progressLoadedBytes: 0,
      progressTotalBytes: null,
      result: null,
      errorCode: null,
      errorMessage: null,
      requestToken,
    });

    let events;
    try {
      events = this.api.uploadFiles(
        {
          sourceId: state.context.sourceId,
          directoryPath: state.context.directoryPath,
        },
        state.files,
      );
    } catch (error: unknown) {
      this.failRequest(error, requestToken);
      return true;
    }

    this.activeSubscription = events.subscribe({
      next: (event) => this.handleEvent(event, requestToken),
      error: (error: unknown) => this.failRequest(error, requestToken),
      complete: () => this.handleIncompleteResponse(requestToken),
    });
    return true;
  }

  cancel(): boolean {
    const state = this.state();
    if (state.phase !== 'uploading') {
      return false;
    }

    const requestToken = ++this.nextRequestToken;
    this.releaseActiveRequest();
    this.mutableState.set({
      ...state,
      phase: 'cancelled',
      errorCode: null,
      errorMessage: null,
      requestToken,
    });
    return true;
  }

  close(): boolean {
    if (this.isPending()) {
      return false;
    }

    this.releaseActiveRequest();
    this.completionCallback = null;
    this.mutableState.set(closedState(++this.nextRequestToken));
    return true;
  }

  reset(): void {
    this.releaseActiveRequest();
    this.completionCallback = null;
    this.limitsCache = null;
    this.mutableState.set(closedState(++this.nextRequestToken));
  }

  private async loadLimits(requestToken: number): Promise<void> {
    this.mutableState.update((state) =>
      state.requestToken === requestToken
        ? {
            ...state,
            limitsPending: true,
            errorCode: null,
            errorMessage: null,
          }
        : state,
    );

    let request: Promise<UploadLimitsDto>;
    try {
      request = this.api.getUploadLimits();
    } catch (error: unknown) {
      request = Promise.reject(error);
    }

    try {
      const limits = await request;
      this.limitsCache = Object.freeze({ ...limits });
      const current = this.state();
      if (current.requestToken !== requestToken || current.phase === 'closed') {
        return;
      }

      this.mutableState.set(
        withFiles({
          ...current,
          limits: this.limitsCache,
          limitsPending: false,
          errorCode: null,
          errorMessage: null,
        }),
      );
    } catch {
      const current = this.state();
      if (current.requestToken !== requestToken || current.phase === 'closed') {
        return;
      }

      this.mutableState.set({
        ...current,
        limitsPending: false,
        errorCode: 'upload_limits_unavailable',
        errorMessage: 'Upload limits could not be loaded.',
      });
    }
  }

  private handleEvent(event: UploadEvent, requestToken: number): void {
    const state = this.state();
    if (
      state.requestToken !== requestToken ||
      (state.phase !== 'uploading' && state.phase !== 'finalizing')
    ) {
      return;
    }

    if (event.kind === 'progress') {
      const loadedBytes = Math.max(0, event.loadedBytes);
      const totalBytes = event.totalBytes === null ? null : Math.max(0, event.totalBytes);
      this.mutableState.set({
        ...state,
        phase: totalBytes !== null && loadedBytes >= totalBytes ? 'finalizing' : 'uploading',
        progressLoadedBytes: loadedBytes,
        progressTotalBytes: totalBytes,
      });
      return;
    }

    this.activeSubscription?.unsubscribe();
    this.activeSubscription = null;
    this.mutableState.set({
      ...state,
      phase: 'completed',
      result: event.result,
      errorCode: null,
      errorMessage: null,
    });
    const callback = this.completionCallback;
    this.completionCallback = null;
    try {
      callback?.();
    } catch {
      // Upload completion remains authoritative if the caller's refresh hook fails.
    }
  }

  private failRequest(error: unknown, requestToken: number): void {
    const state = this.state();
    if (
      state.requestToken !== requestToken ||
      (state.phase !== 'uploading' && state.phase !== 'finalizing')
    ) {
      return;
    }

    this.releaseActiveRequest();
    const code = problemCode(error);
    this.mutableState.set({
      ...state,
      phase: 'failed',
      result: null,
      errorCode: code,
      errorMessage: uploadErrorMessages[code] ?? 'The upload could not be completed.',
    });
  }

  private handleIncompleteResponse(requestToken: number): void {
    const state = this.state();
    if (
      state.requestToken === requestToken &&
      (state.phase === 'uploading' || state.phase === 'finalizing')
    ) {
      this.failRequest(new Error('Upload response was incomplete.'), requestToken);
    }
  }

  private releaseActiveRequest(): void {
    this.activeSubscription?.unsubscribe();
    this.activeSubscription = null;
  }
}

function reviewState(
  context: UploadContext,
  files: readonly File[],
  limits: UploadLimitsDto | null,
  requestToken: number,
): UploadState {
  return withFiles({
    phase: 'review',
    context,
    files,
    limits,
    limitsPending: limits === null,
    totalBytes: 0,
    preflightIssues: [],
    progressLoadedBytes: 0,
    progressTotalBytes: null,
    result: null,
    errorCode: null,
    errorMessage: null,
    requestToken,
  });
}

function withFiles(state: UploadState): UploadState {
  const totalBytes = state.files.reduce((total, file) => total + file.size, 0);
  return {
    ...state,
    totalBytes,
    preflightIssues: preflightIssues(state.files, totalBytes, state.limits),
  };
}

function preflightIssues(
  files: readonly File[],
  totalBytes: number,
  limits: UploadLimitsDto | null,
): readonly UploadPreflightIssue[] {
  if (files.length === 0) {
    return [
      {
        code: 'upload_empty',
        message: 'Choose at least one file to upload.',
        fileName: null,
      },
    ];
  }

  if (limits === null) {
    return [];
  }

  const issues: UploadPreflightIssue[] = [];
  if (files.length > limits.maxFilesPerBatch) {
    issues.push({
      code: 'upload_too_many_files',
      message: `Choose no more than ${limits.maxFilesPerBatch} files.`,
      fileName: null,
    });
  }

  const oversized = files.find((file) => file.size > limits.maxFileBytes);
  if (oversized) {
    issues.push({
      code: 'upload_file_too_large',
      message: `${oversized.name} exceeds the per-file upload limit.`,
      fileName: oversized.name,
    });
  }

  if (totalBytes > limits.maxBatchBytes) {
    issues.push({
      code: 'upload_batch_too_large',
      message: 'The selected files exceed the batch upload limit.',
      fileName: null,
    });
  }

  return Object.freeze(issues);
}

function problemCode(error: unknown): string {
  if (
    !(error instanceof HttpErrorResponse) ||
    typeof error.error !== 'object' ||
    error.error === null ||
    !('code' in error.error) ||
    typeof error.error.code !== 'string' ||
    !(error.error.code in uploadErrorMessages)
  ) {
    return 'request_failed';
  }

  return error.error.code;
}

function closedState(requestToken = 0): UploadState {
  return {
    phase: 'closed',
    context: null,
    files: [],
    limits: null,
    limitsPending: false,
    totalBytes: 0,
    preflightIssues: [],
    progressLoadedBytes: 0,
    progressTotalBytes: null,
    result: null,
    errorCode: null,
    errorMessage: null,
    requestToken,
  };
}
