import { HttpErrorResponse } from '@angular/common/http';
import { DestroyRef, Inject, Injectable, InjectionToken, computed, signal } from '@angular/core';
import { CommanderApiPort, MediaPreviewDto } from '../api/api.models';
import { ProtectedStateResetService } from '../auth/protected-state-reset.service';
import {
  AdjustedSubtitleCue,
  MediaPreviewClientError,
  MediaPreviewContext,
  MediaPreviewState,
  SubtitleCandidate,
} from './media-preview.models';

const pollMilliseconds = 1_000;
const maximumPollAttempts = 6_000;
const maximumOffsetMilliseconds = 600_000;

export interface MediaPreviewScheduler {
  schedule(callback: () => Promise<void> | void, delayMilliseconds: number): unknown;
  cancel(handle: unknown): void;
}

export const MEDIA_PREVIEW_SCHEDULER = new InjectionToken<MediaPreviewScheduler>(
  'MEDIA_PREVIEW_SCHEDULER',
  {
    providedIn: 'root',
    factory: () => ({
      schedule: (callback, delay) => setTimeout(() => void callback(), delay),
      cancel: (handle) => clearTimeout(handle as ReturnType<typeof setTimeout>),
    }),
  },
);

@Injectable({ providedIn: 'root' })
export class MediaPreviewStore {
  private readonly mutableState = signal<MediaPreviewState>(closedState());
  private nextRequestToken = 0;
  private pollHandle: unknown | null = null;
  private pollAttempts = 0;
  private opener: HTMLElement | null = null;
  private disposed = false;
  private completionHandler: (() => void) | null = null;

  readonly state = this.mutableState.asReadonly();
  readonly adjustedCues = computed<readonly AdjustedSubtitleCue[]>(() => {
    const state = this.state();
    return (state.session?.cues ?? []).map((cue) => ({
      ...cue,
      startMilliseconds: Math.max(0, cue.startMilliseconds + state.offsetMilliseconds),
      endMilliseconds: Math.max(0, cue.endMilliseconds + state.offsetMilliseconds),
    }));
  });
  readonly activeCue = computed(() => {
    const time = this.state().videoTimeMilliseconds;
    return this.adjustedCues().find((cue) =>
      cue.endMilliseconds > cue.startMilliseconds &&
      time >= cue.startMilliseconds &&
      time < cue.endMilliseconds,
    ) ?? null;
  });
  readonly canPlanSave = computed(() => {
    const state = this.state();
    return state.phase === 'ready' &&
      state.session?.subtitlePath !== null &&
      state.session?.sourceReadOnly === false &&
      state.offsetMilliseconds !== 0;
  });
  readonly canExecuteSave = computed(() =>
    this.state().phase === 'review' && this.state().savePlan?.canExecute === true,
  );
  readonly mediaUrl = computed(() => {
    const session = this.state().session;
    if (!session || session.phase !== 'ready') {
      return null;
    }
    return session.playbackMode === 'direct'
      ? this.api.mediaPreviewContentUrl(session.sessionId)
      : this.api.mediaPreviewHlsUrl(session.sessionId, 'index.m3u8');
  });

  constructor(
    private readonly api: CommanderApiPort,
    @Inject(MEDIA_PREVIEW_SCHEDULER) private readonly scheduler: MediaPreviewScheduler,
    protectedState: ProtectedStateResetService,
    destroyRef: DestroyRef,
  ) {
    const unregister = protectedState.register(() => this.reset());
    destroyRef.onDestroy(() => {
      unregister();
      this.dispose();
    });
  }

  setCompletionHandler(handler: () => void): void {
    this.completionHandler = handler;
  }

  async open(context: MediaPreviewContext, opener: HTMLElement | null): Promise<void> {
    if (this.disposed) {
      return;
    }
    const previousSessionId = this.state().session?.sessionId;
    this.invalidatePolling();
    if (previousSessionId) {
      void this.api.closeMediaPreview(previousSessionId).catch(() => undefined);
    }
    const captured = Object.freeze({ ...context });
    const token = ++this.nextRequestToken;
    this.opener = opener;
    this.mutableState.set({
      ...closedState(token),
      phase: 'opening',
      context: captured,
    });
    try {
      const [session, subtitleCandidates] = await Promise.all([
        this.api.createMediaPreview({
          sourceId: captured.sourceId,
          videoPath: captured.videoPath,
        }),
        this.loadSubtitleCandidates(captured),
      ]);
      if (this.isCurrent(token, captured)) {
        this.applySession(session, token, captured, subtitleCandidates);
      }
    } catch (error: unknown) {
      this.fail(error, token, captured);
    }
  }

  setOffset(offsetMilliseconds: number): void {
    const state = this.state();
    if (state.phase === 'closed' || !Number.isFinite(offsetMilliseconds)) {
      return;
    }
    const offset = Math.trunc(Math.max(
      -maximumOffsetMilliseconds,
      Math.min(maximumOffsetMilliseconds, offsetMilliseconds),
    ));
    this.mutableState.set({
      ...state,
      phase: state.session?.phase === 'ready' ? 'ready' : state.phase,
      offsetMilliseconds: offset,
      savePlan: null,
      saveResult: null,
      error: null,
    });
  }

  setVideoTime(videoTimeMilliseconds: number): void {
    if (this.state().phase === 'closed' || !Number.isFinite(videoTimeMilliseconds)) {
      return;
    }
    this.mutableState.update((state) => ({
      ...state,
      videoTimeMilliseconds: Math.max(0, Math.trunc(videoTimeMilliseconds)),
    }));
  }

  async selectSubtitle(subtitlePath: string): Promise<void> {
    const state = this.state();
    if (state.phase !== 'ready' || !state.context || !state.session) {
      return;
    }
    const token = ++this.nextRequestToken;
    const context = state.context;
    this.invalidatePolling();
    this.mutableState.set({
      ...state,
      phase: 'selectingSubtitle',
      savePlan: null,
      saveResult: null,
      error: null,
      requestToken: token,
    });
    try {
      const session = await this.api.selectMediaPreviewSubtitle(
        state.session.sessionId,
        subtitlePath,
      );
      if (this.isCurrent(token, context)) {
        this.applySession(session, token, context);
      }
    } catch (error: unknown) {
      this.fail(error, token, context, 'ready');
    }
  }

  async planSave(): Promise<void> {
    const state = this.state();
    if (!this.canPlanSave() || !state.context || !state.session) {
      return;
    }
    const token = ++this.nextRequestToken;
    const context = state.context;
    this.mutableState.set({
      ...state,
      phase: 'planning',
      savePlan: null,
      saveResult: null,
      error: null,
      requestToken: token,
    });
    try {
      const savePlan = await this.api.planMediaPreviewSubtitleSave(
        state.session.sessionId,
        state.offsetMilliseconds,
      );
      if (this.isCurrent(token, context)) {
        this.mutableState.set({
          ...this.state(),
          phase: 'review',
          savePlan: Object.freeze({ ...savePlan }),
          error: null,
        });
      }
    } catch (error: unknown) {
      this.fail(error, token, context, 'ready');
    }
  }

  async executeSave(): Promise<void> {
    const state = this.state();
    if (!this.canExecuteSave() || !state.context || !state.savePlan) {
      return;
    }
    const token = ++this.nextRequestToken;
    const context = state.context;
    const planId = state.savePlan.planId;
    this.mutableState.set({ ...state, phase: 'saving', error: null, requestToken: token });
    try {
      const saveResult = await this.api.executeMediaPreviewSubtitleSave(planId);
      if (this.isCurrent(token, context)) {
        this.mutableState.set({
          ...this.state(),
          phase: 'saved',
          savePlan: null,
          saveResult: Object.freeze({ ...saveResult }),
          error: null,
        });
        this.completionHandler?.();
      }
    } catch (error: unknown) {
      this.fail(error, token, context, 'review');
    }
  }

  async retryWithFallback(): Promise<void> {
    const state = this.state();
    if (!state.context || !state.session || state.phase === 'closed') {
      return;
    }
    const token = ++this.nextRequestToken;
    const context = state.context;
    this.invalidatePolling();
    this.mutableState.set({ ...state, phase: 'transcoding', error: null, requestToken: token });
    try {
      const session = await this.api.requestMediaPreviewFallback(state.session.sessionId);
      if (this.isCurrent(token, context)) {
        this.applySession(session, token, context);
      }
    } catch (error: unknown) {
      this.fail(error, token, context);
    }
  }

  async close(): Promise<void> {
    const sessionId = this.state().session?.sessionId;
    const opener = this.opener;
    this.opener = null;
    this.invalidatePolling();
    this.mutableState.set(closedState(++this.nextRequestToken));
    if (sessionId) {
      try {
        await this.api.closeMediaPreview(sessionId);
      } catch {
        // The opaque session expires and is cleaned server-side if the close request is interrupted.
      }
    }
    queueMicrotask(() => {
      if (opener?.isConnected) {
        opener.focus();
      }
    });
  }

  private applySession(
    session: MediaPreviewDto,
    token: number,
    context: MediaPreviewContext,
    subtitleCandidates: readonly SubtitleCandidate[] = this.state().subtitleCandidates,
  ): void {
    const phase = session.phase;
    this.mutableState.set({
      ...this.state(),
      phase,
      session: Object.freeze({ ...session, cues: Object.freeze([...session.cues]) }),
      subtitleCandidates,
      savePlan: null,
      saveResult: null,
      error: phase === 'failed'
        ? safeSessionFailure(session)
        : null,
      requestToken: token,
    });
    if (phase === 'probing' || phase === 'transcoding') {
      this.schedulePoll(token, context);
    } else {
      this.clearPoll();
    }
  }

  private async loadSubtitleCandidates(
    context: MediaPreviewContext,
  ): Promise<readonly SubtitleCandidate[]> {
    try {
      const entries = await this.api.listFiles(
        context.sourceId,
        parentDirectory(context.videoPath),
      );
      return Object.freeze(entries
        .filter((entry) =>
          entry.type === 'file' &&
          !entry.isSymbolicLink &&
          entry.name.toLocaleLowerCase().endsWith('.srt'),
        )
        .map((entry) => Object.freeze({
          name: entry.name,
          path: entry.relativePath,
        }))
        .sort((left, right) =>
          left.name.localeCompare(right.name, undefined, { sensitivity: 'base' }) ||
          left.path.localeCompare(right.path),
        ));
    } catch {
      // Subtitle discovery is a convenience; preview creation remains authoritative.
      return Object.freeze([]);
    }
  }

  private schedulePoll(token: number, context: MediaPreviewContext): void {
    this.clearPoll();
    if (++this.pollAttempts > maximumPollAttempts) {
      this.mutableState.update((state) => ({
        ...state,
        phase: 'failed',
        error: {
          code: 'media_preview_poll_timeout',
          detail: 'The browser-compatible preview did not become ready in time.',
        },
      }));
      return;
    }
    this.pollHandle = this.scheduler.schedule(
      () => this.poll(token, context),
      pollMilliseconds,
    );
  }

  private async poll(token: number, context: MediaPreviewContext): Promise<void> {
    this.pollHandle = null;
    const sessionId = this.state().session?.sessionId;
    if (!sessionId || !this.isCurrent(token, context)) {
      return;
    }
    try {
      const session = await this.api.getMediaPreview(sessionId);
      if (this.isCurrent(token, context)) {
        this.applySession(session, token, context);
      }
    } catch (error: unknown) {
      if (this.isCurrent(token, context)) {
        const safe = safeProblem(error);
        if (safe.code === 'preview_not_ready' || safe.code === 'media_preview_rate_limited') {
          this.mutableState.update((state) => ({ ...state, error: safe }));
          this.schedulePoll(token, context);
        } else {
          this.mutableState.update((state) => ({ ...state, phase: 'failed', error: safe }));
        }
      }
    }
  }

  private fail(
    error: unknown,
    token: number,
    context: MediaPreviewContext,
    fallbackPhase: 'ready' | 'review' | 'failed' = 'failed',
  ): void {
    if (!this.isCurrent(token, context)) {
      return;
    }
    this.mutableState.update((state) => ({
      ...state,
      phase: fallbackPhase,
      error: safeProblem(error),
    }));
  }

  private reset(): void {
    if (this.disposed) {
      return;
    }
    this.opener = null;
    this.invalidatePolling();
    this.mutableState.set(closedState(++this.nextRequestToken));
  }

  private invalidatePolling(): void {
    this.pollAttempts = 0;
    this.clearPoll();
  }

  private clearPoll(): void {
    if (this.pollHandle !== null) {
      this.scheduler.cancel(this.pollHandle);
      this.pollHandle = null;
    }
  }

  private isCurrent(token: number, context: MediaPreviewContext): boolean {
    const state = this.state();
    return !this.disposed &&
      state.phase !== 'closed' &&
      state.requestToken === token &&
      state.context === context;
  }

  private dispose(): void {
    this.reset();
    this.disposed = true;
  }
}

const safeDetails: Readonly<Record<string, string>> = Object.freeze({
  video_format_unsupported: 'Only MP4, MKV, and AVI videos can be previewed.',
  video_invalid: 'The selected entry is not a supported video file.',
  symbolic_link_rejected: 'Symbolic links cannot be used for media previews.',
  subtitle_invalid: 'The subtitle file is not a valid SRT document.',
  subtitle_too_large: 'The subtitle file exceeds the configured limits.',
  subtitle_encoding_unsupported: 'The subtitle encoding is not supported.',
  subtitle_offset_invalid: 'Choose an offset between -600000 and 600000 milliseconds.',
  subtitle_selection_invalid: 'Select an SRT subtitle from the video directory.',
  subtitle_source_read_only: 'This source is read-only.',
  preview_session_expired: 'The media preview session expired. Open the video again.',
  preview_session_stale: 'The media file changed. Open it again.',
  preview_not_ready: 'The media preview is not ready yet.',
  media_preview_rate_limited: 'Too many preview requests were sent. Try again shortly.',
  preview_capacity_reached: 'The preview queue is full. Try again shortly.',
  media_tools_unavailable: 'The media preview tools are unavailable.',
  media_probe_failed: 'The video could not be inspected.',
  media_transcode_failed: 'A browser-compatible preview could not be created.',
  subtitle_save_plan_expired: 'The save review expired. Review the change again.',
  subtitle_save_plan_stale: 'The subtitle changed. Review the change again.',
  subtitle_save_failed: 'The subtitle could not be saved; the original was preserved.',
  subtitle_recovery_required: 'The original subtitle is in its backup file. Manual recovery is required.',
});

function safeProblem(error: unknown): MediaPreviewClientError {
  if (error instanceof HttpErrorResponse && isRecord(error.error)) {
    const code = typeof error.error['code'] === 'string' ? error.error['code'] : '';
    if (code in safeDetails) {
      return { code, detail: safeDetails[code]! };
    }
  }
  return {
    code: 'media_preview_request_failed',
    detail: 'The media preview request could not be completed.',
  };
}

function safeSessionFailure(session: MediaPreviewDto): MediaPreviewClientError {
  const code = session.failureCode ?? '';
  return code in safeDetails
    ? { code, detail: safeDetails[code]! }
    : {
        code: 'media_preview_failed',
        detail: 'The media preview could not be prepared.',
      };
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null;
}

function parentDirectory(path: string): string {
  const separator = path.lastIndexOf('/');
  return separator <= 0 ? '/' : path.slice(0, separator);
}

function closedState(requestToken = 0): MediaPreviewState {
  return {
    phase: 'closed',
    context: null,
    session: null,
    subtitleCandidates: Object.freeze([]),
    offsetMilliseconds: 0,
    videoTimeMilliseconds: 0,
    savePlan: null,
    saveResult: null,
    error: null,
    requestToken,
  };
}
