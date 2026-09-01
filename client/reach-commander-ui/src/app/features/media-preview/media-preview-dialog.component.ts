import { A11yModule } from '@angular/cdk/a11y';
import {
  AfterViewInit,
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  HostListener,
  ViewChild,
  computed,
  effect,
  inject,
  signal,
} from '@angular/core';
import Hls from 'hls.js';
import { MediaPreviewStore } from '../../core/state/media-preview.store';

@Component({
  selector: 'app-media-preview-dialog',
  imports: [A11yModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './media-preview-dialog.component.html',
  styleUrl: './media-preview-dialog.component.scss',
})
export class MediaPreviewDialogComponent implements AfterViewInit {
  readonly store = inject(MediaPreviewStore);
  readonly busy = computed(() => [
    'opening', 'probing', 'transcoding', 'selectingSubtitle', 'planning', 'saving',
  ].includes(this.store.state().phase));
  readonly statusLabel = computed(() => {
    switch (this.store.state().phase) {
      case 'opening': return 'Opening video';
      case 'probing': return 'Inspecting video';
      case 'transcoding': return 'Preparing browser-compatible video';
      case 'selectingSubtitle': return 'Loading subtitle';
      case 'planning': return 'Preparing save review';
      case 'review': return 'Review corrected subtitle';
      case 'saving': return 'Saving corrected subtitle';
      case 'saved': return 'Subtitle saved';
      case 'failed': return 'Preview failed';
      default: return 'Subtitle synchronization';
    }
  });
  readonly playbackFailed = computed(() =>
    this.store.state().phase === 'ready' && this.playbackError(),
  );

  @ViewChild('dialog', { read: ElementRef, static: true })
  private dialog!: ElementRef<HTMLDialogElement>;

  @ViewChild('video', { read: ElementRef, static: true })
  private video!: ElementRef<HTMLVideoElement>;

  private hls: Hls | null = null;
  private readonly playbackError = signal(false);

  constructor() {
    const destroyRef = inject(DestroyRef);
    destroyRef.onDestroy(() => {
      this.detachPlayback();
      void this.store.close();
    });
    effect(() => {
      const url = this.store.mediaUrl();
      const mode = this.store.state().session?.playbackMode ?? null;
      queueMicrotask(() => this.attachPlayback(url, mode));
    });
  }

  ngAfterViewInit(): void {
    const dialog = this.dialog.nativeElement;
    if (typeof dialog.showModal === 'function') {
      dialog.showModal();
    } else {
      dialog.setAttribute('open', '');
    }
    dialog.focus();
  }

  setExactOffset(event: Event): void {
    const value = Number((event.target as HTMLInputElement | null)?.value);
    if (Number.isFinite(value)) {
      this.store.setOffset(value);
    }
  }

  adjustOffset(deltaMilliseconds: number): void {
    this.store.setOffset(this.store.state().offsetMilliseconds + deltaMilliseconds);
  }

  loadSubtitle(input: HTMLInputElement): void {
    const path = input.value.trim();
    if (path) {
      void this.store.selectSubtitle(path);
    }
  }

  updateVideoTime(): void {
    this.store.setVideoTime(this.video.nativeElement.currentTime * 1000);
  }

  seek(position: 'beginning' | 'middle' | 'end'): void {
    const video = this.video.nativeElement;
    const duration = Number.isFinite(video.duration) ? video.duration : 0;
    video.currentTime = position === 'beginning'
      ? 0
      : position === 'middle'
        ? duration / 2
        : Math.max(0, duration - 0.1);
    this.updateVideoTime();
  }

  reviewSave(): void {
    void this.store.planSave();
  }

  save(): void {
    void this.store.executeSave();
  }

  retryPlayback(): void {
    this.playbackError.set(false);
    void this.store.retryWithFallback();
  }

  requestClose(): void {
    const state = this.store.state();
    if (this.busy()) {
      return;
    }
    if (state.offsetMilliseconds !== 0 &&
        state.saveResult === null &&
        !window.confirm('Discard the unsaved subtitle offset?')) {
      return;
    }
    void this.store.close();
  }

  handleNativeCancel(event: Event): void {
    event.preventDefault();
    this.requestClose();
  }

  @HostListener('keydown', ['$event'])
  handleDialogKeydown(event: KeyboardEvent): void {
    event.stopPropagation();
    if (event.key === 'Escape') {
      event.preventDefault();
      this.requestClose();
      return;
    }
    if (event.key === ' ' && !isTextControl(event.target)) {
      event.preventDefault();
      this.togglePlayback();
    }
  }

  private togglePlayback(): void {
    const video = this.video.nativeElement;
    if (video.paused) {
      void video.play().catch(() => this.onPlaybackError());
    } else {
      video.pause();
    }
  }

  private attachPlayback(
    url: string | null,
    mode: 'direct' | 'hls' | null,
  ): void {
    if (!this.video) {
      return;
    }
    this.detachPlayback();
    this.playbackError.set(false);
    if (!url || !mode) {
      return;
    }
    const video = this.video.nativeElement;
    if (mode === 'direct' || video.canPlayType('application/vnd.apple.mpegurl')) {
      video.src = url;
      video.load();
      return;
    }
    if (!Hls.isSupported()) {
      this.onPlaybackError();
      return;
    }
    this.hls = new Hls({ enableWorker: true });
    this.hls.on(Hls.Events.ERROR, (_event, data) => {
      if (data.fatal) {
        this.onPlaybackError();
      }
    });
    this.hls.loadSource(url);
    this.hls.attachMedia(video);
  }

  private detachPlayback(): void {
    this.hls?.destroy();
    this.hls = null;
    if (this.video) {
      const video = this.video.nativeElement;
      video.pause();
      video.removeAttribute('src');
      video.load();
    }
  }

  onPlaybackError(): void {
    this.playbackError.set(true);
  }
}

function isTextControl(target: EventTarget | null): boolean {
  return target instanceof HTMLInputElement ||
    target instanceof HTMLTextAreaElement ||
    target instanceof HTMLSelectElement;
}
