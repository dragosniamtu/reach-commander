import { HttpErrorResponse } from '@angular/common/http';
import { DestroyRef, Inject, Injectable, InjectionToken, computed, signal } from '@angular/core';
import { CommanderApiPort, MediaPreviewDto, SubtitleRepairStatusDto, TextEncodingKind } from '../api/api.models';
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
const maximumRepairPollFailures = 3;
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
  private repairPollHandle: unknown | null = null;
  private repairToken = 0;
  private repairPollFailures = 0;
  private repairStarting: Promise<SubtitleRepairStatusDto> | null = null;
  private opener: HTMLElement | null = null;
  private disposed = false;
  private completionHandler: (() => void) | null = null;

  readonly state = this.mutableState.asReadonly();
  readonly adjustedCues = computed<readonly AdjustedSubtitleCue[]>(() => {
    const state = this.state();
    if (state.subtitleSelectionFailed || ['selectingSubtitle', 'refreshingSaved', 'savedRefreshFailed'].includes(state.phase)) {
      return [];
    }
    return (state.session?.cues ?? []).map((cue) => ({
      ...cue,
      startMilliseconds: Math.max(0, Math.round(cue.startMilliseconds * state.timeScale) + state.offsetMilliseconds),
      endMilliseconds: Math.max(0, Math.round(cue.endMilliseconds * state.timeScale) + state.offsetMilliseconds),
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
      this.hasReviewedSubtitle(state) &&
      !this.repairActive() &&
      state.session?.sourceReadOnly === false &&
      (state.offsetMilliseconds !== 0 || state.timeScale !== 1 ||
        state.session.subtitleEncoding?.conversionNeeded === true);
  });
  readonly repairActive = computed(() => {
    const status = this.state().repair?.state;
    return status === 'queued' || status === 'running' || status === 'cancelling';
  });
  readonly canStartRepair = computed(() => {
    const state = this.state();
    return (state.phase === 'ready' || state.phase === 'saved') && this.hasReviewedSubtitle(state) &&
      !this.repairActive() && !state.repairStatusUnknown && state.repairCapabilities?.available === true;
  });
  readonly subtitleSelectionError = computed(() => {
    const state = this.state();
    const code = !state.subtitleSelectionFailed ? state.session?.subtitleErrorCode : null;
    return code ? safeCode(code).detail : null;
  });
  readonly repairAvailabilityMessage = computed(() => {
    const capabilities = this.state().repairCapabilities;
    if (!capabilities || capabilities.available) return null;
    return safeCode(capabilities.reasonCode ?? (capabilities.enabled
      ? 'subtitle_repair_unavailable' : 'subtitle_repair_disabled')).detail;
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
    this.invalidateRepair();
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
        await this.checkRepairCapabilities();
      } else {
        try {
          await this.api.closeMediaPreview(session.sessionId);
        } catch {
          // The server heartbeat timeout cleans up if this best-effort close is interrupted.
        }
      }
    } catch (error: unknown) {
      this.fail(error, token, captured);
    }
  }

  setOffset(offsetMilliseconds: number): void {
    const state = this.state();
    if (!this.canEditTiming(state) || !Number.isFinite(offsetMilliseconds)) {
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

  resetCorrection(): void {
    const state = this.state();
    if (!this.canEditTiming(state)) return;
    this.setOffset(0);
    this.mutableState.update((current) => ({ ...current, timeScale: 1, repair: null }));
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

  async selectSubtitle(
    subtitlePath: string,
    sourceEncoding?: TextEncodingKind,
    encodingReviewed?: boolean,
  ): Promise<void> {
    const state = this.state();
    if (!this.canEditTiming(state) || !state.context || !state.session) {
      return;
    }
    const token = ++this.nextRequestToken;
    const context = state.context;
    this.invalidatePolling();
    this.invalidateRepair();
    this.mutableState.set({
      ...state,
      phase: 'selectingSubtitle',
      subtitleSelectionPath: subtitlePath,
      subtitleSelectionFailed: false,
      repair: null,
      savePlan: null,
      saveResult: null,
      error: null,
      requestToken: token,
    });
    try {
      const session = await this.api.selectMediaPreviewSubtitle(
        state.session.sessionId,
        subtitlePath,
        sourceEncoding,
        encodingReviewed,
      );
      if (this.isCurrent(token, context)) {
        if (!encodingReviewed) {
          this.mutableState.update((current) => ({ ...current, offsetMilliseconds: 0, timeScale: 1 }));
        }
        this.applySession(session, token, context);
      }
    } catch (error: unknown) {
      this.fail(error, token, context, 'ready');
      if (this.isCurrent(token, context)) {
        this.mutableState.update((current) => ({ ...current, subtitleSelectionFailed: true }));
        if (state.session.transcodeActive) this.schedulePoll(token, context);
      }
    }
  }

  confirmSubtitleEncoding(): Promise<void> {
    const state = this.state();
    const session = state.session;
    return !state.subtitleSelectionFailed && session?.subtitlePath && session.subtitleEncoding?.requiresReview &&
      (!state.subtitleSelectionPath || state.subtitleSelectionPath === session.subtitlePath)
      ? this.selectSubtitle(session.subtitlePath, session.subtitleEncoding.sourceEncoding, true)
      : Promise.resolve();
  }

  async checkRepairCapabilities(): Promise<void> {
    const state = this.state();
    if (!state.context || !state.session || state.checkingRepairCapabilities) return;
    const context = state.context;
    this.mutableState.update((current) => ({ ...current, checkingRepairCapabilities: true }));
    let repairCapabilities;
    try {
      repairCapabilities = await this.api.getSubtitleRepairCapabilities();
    } catch {
      repairCapabilities = { enabled: true, available: false };
    }
    if (!this.disposed && this.state().context === context) {
      this.mutableState.update((current) => ({ ...current, checkingRepairCapabilities: false, repairCapabilities }));
    }
  }

  async startRepair(): Promise<void> {
    const sessionId = this.state().session?.sessionId;
    if (!sessionId || !this.canStartRepair()) return;
    const token = ++this.repairToken;
    this.repairPollFailures = 0;
    this.mutableState.update((state) => ({
      ...state,
      repair: { state: 'queued', stage: 'Starting timing correction', percent: 0 },
      error: null,
    }));
    const starting = this.api.startSubtitleRepair(sessionId);
    this.repairStarting = starting;
    try {
      const status = await starting;
      if (token === this.repairToken) this.applyRepairStatus(sessionId, token, status);
    } catch (error: unknown) {
      if (token === this.repairToken) this.failRepair(safeProblem(error));
    } finally {
      if (this.repairStarting === starting) this.repairStarting = null;
    }
  }

  async cancelRepair(): Promise<void> {
    const state = this.state();
    const sessionId = state.session?.sessionId;
    if (!sessionId || state.repair?.state === 'cancelling' ||
        (!this.repairActive() && !state.repairStatusUnknown) ||
        !['ready', 'review', 'saved'].includes(state.phase)) return;
    const token = ++this.repairToken;
    this.repairPollFailures = 0;
    this.clearRepairPoll();
    this.mutableState.set({
      ...state,
      repair: { state: 'cancelling', stage: 'Stopping timing correction', percent: state.repair?.percent ?? 0 },
      repairStatusUnknown: false,
    });
    await this.repairStarting?.catch(() => undefined);
    if (token !== this.repairToken) return;
    try {
      const status = await this.api.cancelSubtitleRepair(sessionId);
      if (token === this.repairToken) this.applyRepairStatus(sessionId, token, status, false);
    } catch (error: unknown) {
      if (token !== this.repairToken) return;
      this.handleRepairStatusLoss(safeProblem(error));
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
        state.timeScale === 1 ? undefined : state.timeScale,
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
          phase: 'refreshingSaved',
          savePlan: null,
          saveResult: Object.freeze({ ...saveResult }),
          error: null,
        });
        this.completionHandler?.();
        await this.refreshSavedSubtitle();
      }
    } catch (error: unknown) {
      this.fail(error, token, context, 'review');
    }
  }

  async refreshSavedSubtitle(): Promise<void> {
    const state = this.state();
    const { context, session, saveResult } = state;
    if (!context || !session || !saveResult ||
        (state.phase !== 'refreshingSaved' && state.phase !== 'savedRefreshFailed')) return;
    const token = ++this.nextRequestToken;
    this.invalidatePolling();
    this.mutableState.set({ ...state, phase: 'refreshingSaved', error: null, requestToken: token });
    try {
      const refreshed = await this.api.getMediaPreview(session.sessionId);
      if (this.isCurrent(token, context)) {
        if (refreshed.phase !== 'ready' || refreshed.subtitlePath !== saveResult.subtitlePath) {
          throw new Error('Saved subtitle not available in refreshed preview');
        }
        this.mutableState.update((current) => ({
          ...current, offsetMilliseconds: 0, timeScale: 1, repair: null,
        }));
        this.applySession(refreshed, token, context);
      }
    } catch {
      if (this.isCurrent(token, context)) {
        this.mutableState.update((current) => ({
          ...current, phase: 'savedRefreshFailed', error: safeCode('subtitle_saved_refresh_failed'),
        }));
      }
    }
  }

  async retryWithFallback(): Promise<void> {
    const state = this.state();
    if (!state.context || !state.session || this.repairActive() ||
        !['ready', 'failed'].includes(state.phase)) {
      return;
    }
    const token = ++this.nextRequestToken;
    const context = state.context;
    this.invalidatePolling();
    this.mutableState.set({ ...state, phase: 'queued', error: null, requestToken: token });
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
    const state = this.state();
    if (state.phase === 'closed') return;
    const sessionId = state.session?.sessionId;
    const opener = this.opener;
    this.opener = null;
    this.invalidatePolling();
    this.invalidateRepair();
    const token = ++this.nextRequestToken;
    this.mutableState.set(closedState(token));
    if (sessionId) {
      try {
        await this.api.closeMediaPreview(sessionId);
      } catch {
        // The opaque session expires and is cleaned server-side if the close request is interrupted.
      }
    }
    queueMicrotask(() => {
      if (opener?.isConnected && this.state().requestToken === token) {
        opener.focus();
      }
    });
  }

  private hasReviewedSubtitle(state: MediaPreviewState): boolean {
    const session = state.session;
    return !state.subtitleSelectionFailed && !!session?.subtitlePath &&
      (!state.subtitleSelectionPath || state.subtitleSelectionPath === session.subtitlePath) &&
      !session.subtitleEncoding?.requiresReview;
  }

  private canEditTiming(state: MediaPreviewState): boolean {
    return !this.repairActive() && ['ready', 'review', 'saved'].includes(state.phase);
  }

  private applyRepairStatus(
    sessionId: string,
    token: number,
    status: SubtitleRepairStatusDto,
    applyCorrection = true,
  ): void {
    if (status.state === 'failed' && applyCorrection) {
      this.failRepair(safeCode(status.errorCode ?? 'subtitle_repair_failed'));
      return;
    }
    if (!applyCorrection && ['idle', 'completed', 'failed'].includes(status.state)) {
      status = { state: 'cancelled', stage: 'Timing correction cancelled', percent: 0 };
    }
    if (status.state === 'completed' && applyCorrection) {
      const { offsetMilliseconds, timeScale } = status;
      if (offsetMilliseconds == null || !Number.isFinite(offsetMilliseconds) ||
          timeScale == null || !Number.isFinite(timeScale) || timeScale <= 0) {
        this.failRepair(safeCode('subtitle_repair_failed'));
        return;
      }
      this.mutableState.update((state) => ({
        ...state,
        phase: state.phase === 'saved' ? 'ready' : state.phase,
        repair: status,
        offsetMilliseconds,
        timeScale,
        saveResult: null,
        error: null,
      }));
    } else {
      this.mutableState.update((state) => ({ ...state, repair: status, error: null }));
    }
    if (this.repairActive()) this.scheduleRepairPoll(sessionId, token, applyCorrection);
  }

  private scheduleRepairPoll(sessionId: string, token: number, applyCorrection: boolean): void {
    this.repairPollHandle = this.scheduler.schedule(async () => {
      this.repairPollHandle = null;
      try {
        const status = await this.api.getSubtitleRepair(sessionId);
        if (token === this.repairToken) {
          this.repairPollFailures = 0;
          this.applyRepairStatus(sessionId, token, status, applyCorrection);
        }
      } catch (error: unknown) {
        if (token !== this.repairToken) return;
        const safe = safeProblem(error);
        if (!retryableRepairRequest(error) || ++this.repairPollFailures >= maximumRepairPollFailures) {
          this.handleRepairStatusLoss(safe);
        } else {
          this.mutableState.update((state) => ({ ...state, error: safe }));
          this.scheduleRepairPoll(sessionId, token, applyCorrection);
        }
      }
    }, pollMilliseconds);
  }

  private handleRepairStatusLoss(error: MediaPreviewClientError): void {
    this.invalidateRepair();
    if (error.code === 'preview_session_expired' || error.code === 'preview_session_stale') {
      this.failRepair(error);
    } else {
      this.mutableState.update((state) => ({
        ...state, repair: null, repairStatusUnknown: true, error: null,
      }));
    }
  }

  private failRepair(error: MediaPreviewClientError): void {
    this.mutableState.update((state) => ({
      ...state, repair: { state: 'failed', stage: 'Timing correction failed', percent: 0 }, error,
    }));
    if (error.code === 'subtitle_repair_disabled' || error.code === 'subtitle_repair_unavailable') {
      this.mutableState.update((state) => ({
        ...state, repairCapabilities: {
          enabled: error.code !== 'subtitle_repair_disabled', available: false, reasonCode: error.code,
        },
      }));
    }
  }

  private clearRepairPoll(): void {
    if (this.repairPollHandle !== null) {
      this.scheduler.cancel(this.repairPollHandle);
      this.repairPollHandle = null;
    }
  }

  private invalidateRepair(): void {
    ++this.repairToken;
    this.clearRepairPoll();
    this.repairStarting = null;
  }

  private applySession(
    session: MediaPreviewDto,
    token: number,
    context: MediaPreviewContext,
    subtitleCandidates: readonly SubtitleCandidate[] = this.state().subtitleCandidates,
  ): void {
    const state = this.state();
    const phase = session.phase === 'ready' && state.saveResult ? 'saved' : session.phase;
    this.mutableState.set({
      ...state,
      phase,
      session: Object.freeze({ ...session, cues: Object.freeze([...session.cues]) }),
      subtitleCandidates,
      savePlan: null,
      error: phase === 'failed'
        ? safeSessionFailure(session)
        : null,
      requestToken: token,
    });
    if (phase === 'probing' ||
        phase === 'queued' ||
        phase === 'transcoding' ||
        session.transcodeActive) {
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
    this.invalidateRepair();
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
  subtitle_missing: 'Select an SRT subtitle to continue.',
  subtitle_repair_disabled: 'Automatic timing repair is disabled on this server.',
  subtitle_repair_unavailable: 'Automatic timing repair needs a running, up-to-date subtitle worker.',
  subtitle_repair_busy: 'Another automatic timing repair is running. Try again when it finishes.',
  subtitle_repair_limit: 'The video or subtitle exceeds the limits for automatic timing repair.',
  subtitle_repair_failed: 'Automatic timing repair could not estimate a correction. You can adjust the timing manually.',
  subtitle_encoding_review_required: 'Confirm the decoded subtitle text before continuing.',
  subtitle_no_audio: 'No usable audio was found for automatic timing repair.',
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
  subtitle_saved_refresh_failed: 'The subtitle was saved, but the player could not reload it. Reload the subtitle preview or close and reopen the video.',
  subtitle_recovery_required: 'The original subtitle is in its backup file. Manual recovery is required.',
});

function safeProblem(error: unknown): MediaPreviewClientError {
  if (error instanceof HttpErrorResponse && isRecord(error.error)) {
    const code = typeof error.error['code'] === 'string' ? error.error['code'] : '';
    return safeCode(code);
  }
  return safeCode('');
}

function safeCode(code: string): MediaPreviewClientError {
  return Object.hasOwn(safeDetails, code) ? { code, detail: safeDetails[code]! } : {
    code: 'media_preview_request_failed',
    detail: 'The media preview request could not be completed.',
  };
}

function retryableRepairRequest(error: unknown): boolean {
  return error instanceof HttpErrorResponse &&
    (error.status === 0 || error.status === 429 || error.status >= 500);
}

function safeSessionFailure(session: MediaPreviewDto): MediaPreviewClientError {
  const code = session.failureCode ?? '';
  return Object.hasOwn(safeDetails, code)
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
    subtitleSelectionPath: null,
    subtitleSelectionFailed: false,
    offsetMilliseconds: 0,
    timeScale: 1,
    repair: null,
    repairStatusUnknown: false,
    repairCapabilities: null,
    checkingRepairCapabilities: false,
    videoTimeMilliseconds: 0,
    savePlan: null,
    saveResult: null,
    error: null,
    requestToken,
  };
}
