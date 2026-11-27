import { HttpErrorResponse } from '@angular/common/http';
import { DestroyRef, Injectable, InjectionToken, computed, inject, signal } from '@angular/core';
import { ProtectedStateResetService } from '../../core/auth/protected-state-reset.service';
import { SubtitleGenerationApi } from './subtitle-generation.api';
import {
  SubtitleContext,
  SubtitleCapabilities,
  SubtitleJobStatus,
  subtitleJobActive,
} from './subtitle-generation.models';

export const SUBTITLE_POLL_SCHEDULER = new InjectionToken<{
  schedule(callback: () => Promise<void> | void, delay: number): unknown;
  cancel(handle: unknown): void;
}>('SUBTITLE_POLL_SCHEDULER', {
  providedIn: 'root',
  factory: () => ({
    schedule: (callback, delay) => setTimeout(() => void callback(), delay),
    cancel: (handle) => clearTimeout(handle as ReturnType<typeof setTimeout>),
  }),
});

@Injectable({ providedIn: 'root' })
export class SubtitleGenerationStore {
  private readonly api = inject(SubtitleGenerationApi);
  private readonly scheduler = inject(SUBTITLE_POLL_SCHEDULER);
  private readonly mutableJobs = signal<readonly SubtitleJobStatus[]>([]);
  private readonly cancelling = signal<ReadonlySet<string>>(new Set());
  private pollHandle: unknown | null = null;
  private session = 0;
  private listVersion = 0;
  private capabilitiesVersion = 0;
  private completionHandler: (() => void) | null = null;

  readonly jobs = this.mutableJobs.asReadonly();
  readonly dialog = signal<'closed' | 'configure' | 'jobs'>('closed');
  readonly context = signal<SubtitleContext | null>(null);
  readonly english = signal(true);
  readonly romanian = signal(false);
  readonly skipExisting = signal(true);
  readonly submitting = signal(false);
  readonly loading = signal(false);
  readonly capabilities = signal<SubtitleCapabilities | null>(null);
  readonly capabilitiesLoading = signal(false);
  readonly capabilitiesError = signal(false);
  readonly error = signal<string | null>(null);
  readonly selectedJobId = signal<string | null>(null);
  readonly activeCount = computed(() => this.jobs().filter(subtitleJobActive).length);
  readonly selectedJob = computed(
    () => this.jobs().find((job) => job.jobId === this.selectedJobId()) ?? this.jobs()[0] ?? null,
  );
  readonly serviceReady = computed(() => {
    const capabilities = this.capabilities();
    return (
      !this.capabilitiesLoading() &&
      !this.capabilitiesError() &&
      capabilities?.enabled === true &&
      capabilities.workerAvailable &&
      capabilities.reasonCode !== 'subtitle_state_unavailable'
    );
  });
  readonly englishAvailable = computed(
    () => this.serviceReady() && this.capabilities()?.englishAvailable === true,
  );
  readonly romanianAvailable = computed(
    () => this.serviceReady() && this.capabilities()?.romanianAvailable === true,
  );
  readonly readinessMessage = computed(() => {
    const capabilities = this.capabilities();
    if (this.capabilitiesLoading()) return 'Checking subtitle availability…';
    if (this.capabilitiesError() || capabilities?.reasonCode === 'subtitle_state_unavailable')
      return 'Subtitle readiness could not be checked. Try again, or ask your administrator to check the subtitle service.';
    if (!capabilities) return 'Subtitle availability has not been checked yet.';
    if (!capabilities.enabled)
      return 'Subtitle generation is disabled on this server. Ask your administrator to enable the free CPU subtitle service.';
    if (!capabilities.workerAvailable)
      return 'The subtitle worker is offline. Ask your administrator to start the subtitle service, then recheck.';
    if (!capabilities.englishAvailable && !capabilities.romanianAvailable)
      return 'No subtitle language models are ready. Ask your administrator to finish subtitle model setup, then recheck.';
    if (!capabilities.englishAvailable)
      return 'English is not set up on this server. Romanian is available. Ask your administrator to install the English model.';
    if (!capabilities.romanianAvailable)
      return 'Romanian is not set up on this server. English is available. Ask your administrator to install the Romanian model.';
    return null;
  });
  readonly canGenerate = computed(
    () =>
      this.context() !== null &&
      (this.english() || this.romanian()) &&
      !this.submitting() &&
      this.serviceReady() &&
      (!this.english() || this.englishAvailable()) &&
      (!this.romanian() || this.romanianAvailable()),
  );

  constructor() {
    const unregister = inject(ProtectedStateResetService).register(() => this.reset());
    inject(DestroyRef).onDestroy(() => {
      unregister();
      this.reset();
    });
  }

  setCompletionHandler(handler: () => void): void {
    this.completionHandler = handler;
  }

  open(context: SubtitleContext): void {
    if (this.submitting()) return;
    this.context.set(Object.freeze({ ...context }));
    this.english.set(true);
    this.romanian.set(false);
    this.skipExisting.set(true);
    this.error.set(null);
    this.dialog.set('configure');
    void this.loadCapabilities();
  }

  openHistory(): void {
    this.dialog.set('jobs');
    void this.loadCapabilities();
    void this.restore();
  }

  close(): void {
    this.dialog.set('closed');
  }

  async loadCapabilities(): Promise<void> {
    const session = this.session;
    const version = ++this.capabilitiesVersion;
    this.capabilitiesLoading.set(true);
    this.capabilitiesError.set(false);
    try {
      const capabilities = await this.api.capabilities();
      if (session !== this.session || version !== this.capabilitiesVersion) return;
      this.capabilities.set(capabilities);
      if (!capabilities.enabled) this.clearPoll();
      else if (this.activeCount()) this.schedulePoll();
    } catch {
      if (session !== this.session || version !== this.capabilitiesVersion) return;
      this.capabilitiesError.set(true);
    } finally {
      if (session === this.session && version === this.capabilitiesVersion)
        this.capabilitiesLoading.set(false);
    }
  }

  async restore(): Promise<void> {
    if (this.submitting() || this.cancelling().size > 0) return;
    this.clearPoll();
    const session = this.session;
    const version = ++this.listVersion;
    this.loading.set(true);
    try {
      const jobs = await this.api.list();
      if (session !== this.session || version !== this.listVersion) return;
      const previous = this.jobs();
      this.mutableJobs.set(jobs);
      if (this.dialog() !== 'configure') this.error.set(null);
      if (
        jobs.some(
          (job) =>
            !subtitleJobActive(job) &&
            previous.some((old) => old.jobId === job.jobId && subtitleJobActive(old)),
        )
      ) {
        this.completionHandler?.();
      }
      if (jobs.some(subtitleJobActive)) this.schedulePoll();
    } catch (error) {
      if (session !== this.session || version !== this.listVersion) return;
      this.error.set(
        safeError(error, 'Subtitle jobs could not be loaded. Use Refresh jobs to try again.'),
      );
      if (!(error instanceof HttpErrorResponse && error.status === 503)) this.schedulePoll(5000);
    } finally {
      if (session === this.session && version === this.listVersion) this.loading.set(false);
    }
  }

  async generate(): Promise<void> {
    const context = this.context();
    if (!this.canGenerate() || !context) return;
    const session = this.session;
    this.invalidateList();
    this.submitting.set(true);
    this.error.set(null);
    try {
      const job = await this.api.create({
        sourceId: context.sourceId,
        path: context.path,
        english: this.english(),
        romanian: this.romanian(),
        skipExisting: this.skipExisting(),
      });
      if (session !== this.session) return;
      this.applyJob(job);
      this.selectedJobId.set(job.jobId);
      if (this.dialog() !== 'closed') this.dialog.set('jobs');
    } catch (error) {
      if (session === this.session) {
        this.error.set(safeError(error, 'Subtitle generation could not be started.'));
        if (error instanceof HttpErrorResponse && error.status === 503)
          void this.loadCapabilities();
      }
    } finally {
      if (session === this.session) {
        this.submitting.set(false);
        if (this.activeCount()) this.schedulePoll();
      }
    }
  }

  canCancel(job: SubtitleJobStatus): boolean {
    return (
      subtitleJobActive(job) && job.phase !== 'cancelling' && !this.cancelling().has(job.jobId)
    );
  }

  async cancel(jobId: string): Promise<void> {
    const job = this.jobs().find((candidate) => candidate.jobId === jobId);
    if (!job || !this.canCancel(job)) return;
    const session = this.session;
    this.invalidateList();
    this.cancelling.update((ids) => new Set([...ids, jobId]));
    this.error.set(null);
    try {
      const updated = await this.api.cancel(jobId);
      if (session === this.session) this.applyJob(updated);
    } catch (error) {
      if (session === this.session)
        this.error.set(safeError(error, 'Cancellation failed. You can try again.'));
    } finally {
      if (session === this.session) {
        this.cancelling.update((ids) => new Set([...ids].filter((id) => id !== jobId)));
        if (this.activeCount()) this.schedulePoll();
      }
    }
  }

  reset(): void {
    this.session++;
    this.capabilitiesVersion++;
    this.invalidateList();
    this.mutableJobs.set([]);
    this.cancelling.set(new Set());
    this.context.set(null);
    this.selectedJobId.set(null);
    this.dialog.set('closed');
    this.submitting.set(false);
    this.english.set(true);
    this.romanian.set(false);
    this.skipExisting.set(true);
    this.error.set(null);
    this.capabilities.set(null);
    this.capabilitiesLoading.set(false);
    this.capabilitiesError.set(false);
  }

  private applyJob(job: SubtitleJobStatus): void {
    const previous = this.jobs().find((candidate) => candidate.jobId === job.jobId);
    this.mutableJobs.update((jobs) => [
      job,
      ...jobs.filter((candidate) => candidate.jobId !== job.jobId),
    ]);
    if (previous && subtitleJobActive(previous) && !subtitleJobActive(job))
      this.completionHandler?.();
  }

  private schedulePoll(delay = 2000): void {
    this.clearPoll();
    if (this.capabilities()?.enabled === false) return;
    this.pollHandle = this.scheduler.schedule(() => {
      this.pollHandle = null;
      return this.restore();
    }, delay);
  }

  private invalidateList(): void {
    this.listVersion++;
    this.clearPoll();
    this.loading.set(false);
  }

  private clearPoll(): void {
    if (this.pollHandle !== null) {
      this.scheduler.cancel(this.pollHandle);
      this.pollHandle = null;
    }
  }
}

function safeError(error: unknown, fallback: string): string {
  return error instanceof HttpErrorResponse && typeof error.error?.detail === 'string'
    ? error.error.detail
    : fallback;
}
