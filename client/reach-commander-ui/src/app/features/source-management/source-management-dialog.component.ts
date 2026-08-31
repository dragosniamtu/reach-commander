import { A11yModule } from '@angular/cdk/a11y';
import {
  ChangeDetectionStrategy,
  Component,
  AfterViewInit,
  ElementRef,
  HostListener,
  ViewChild,
  computed,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { SourceAccess, SourceManagementPhase } from '../../core/api/api.models';
import { SourceManagementStore } from '../../core/state/source-management.store';

@Component({
  selector: 'app-source-management-dialog',
  imports: [A11yModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './source-management-dialog.component.html',
  styleUrl: './source-management-dialog.component.scss',
})
export class SourceManagementDialogComponent implements AfterViewInit {
  readonly store = inject(SourceManagementStore);
  readonly opener = input<HTMLElement | null>(null);
  readonly closed = output<void>();
  readonly displayName = signal('');
  readonly hostPath = signal('');
  readonly access = signal<SourceAccess>('readOnly');
  readonly readWriteConfirmed = signal(false);
  readonly displayNameError = computed(() => validateSourceDisplayName(this.displayName()));
  readonly hostPathError = computed(() => validateUbuntuHostPath(this.hostPath()));
  readonly canSubmit = computed(() =>
    !this.store.pending() && this.store.operation() === null &&
    this.displayNameError() === null && this.hostPathError() === null &&
    (this.access() === 'readOnly' || this.readWriteConfirmed()),
  );
  readonly canClose = computed(() => !this.store.pending());

  @ViewChild('displayNameInput', { read: ElementRef })
  private displayNameInput?: ElementRef<HTMLInputElement>;

  ngAfterViewInit(): void {
    queueMicrotask(() => this.displayNameInput?.nativeElement.focus());
  }

  setDisplayName(value: string): void {
    this.displayName.set(value);
  }

  setHostPath(value: string): void {
    this.hostPath.set(value);
  }

  setAccess(access: SourceAccess): void {
    this.access.set(access);
    if (access === 'readOnly') {
      this.readWriteConfirmed.set(false);
    }
  }

  setReadWriteConfirmed(confirmed: boolean): void {
    this.readWriteConfirmed.set(confirmed);
  }

  displayNameChanged(event: Event): void {
    this.setDisplayName((event.target as HTMLInputElement).value);
  }

  hostPathChanged(event: Event): void {
    this.setHostPath((event.target as HTMLInputElement).value);
  }

  accessChanged(event: Event): void {
    this.setAccess((event.target as HTMLInputElement).value as SourceAccess);
  }

  readWriteConfirmationChanged(event: Event): void {
    this.setReadWriteConfirmed((event.target as HTMLInputElement).checked);
  }

  async submit(): Promise<void> {
    if (!this.canSubmit()) {
      return;
    }
    await this.store.submit({
      displayName: this.displayName().trim(),
      hostPath: this.hostPath(),
      access: this.access(),
    });
  }

  close(): void {
    if (!this.canClose()) {
      return;
    }
    this.store.close();
    this.closed.emit();
    this.opener()?.focus();
  }

  phaseLabel(phase: SourceManagementPhase): string {
    switch (phase) {
      case 'accepted': return 'Request accepted';
      case 'validating': return 'Validating host folder';
      case 'applying': return 'Saving source configuration';
      case 'restarting': return 'Restarting ReachCommander';
      case 'healthChecking': return 'Checking server health';
      case 'completed': return 'Source added';
      case 'rolledBack': return 'Previous configuration restored';
      case 'failed': return 'Source could not be added';
    }
  }

  @HostListener('keydown', ['$event'])
  handleKeydown(event: KeyboardEvent): void {
    event.stopPropagation();
    if (event.key === 'Escape') {
      event.preventDefault();
      this.close();
    } else if (event.key === 'Enter' && this.store.operation() === null) {
      event.preventDefault();
      void this.submit();
    }
  }
}

export function validateSourceDisplayName(name: string): string | null {
  const trimmed = name.trim();
  if (trimmed.length === 0) {
    return 'Enter a display name.';
  }
  if (trimmed.length > 80) {
    return 'The display name cannot exceed 80 characters.';
  }
  if ([...trimmed].some(isControl)) {
    return 'The display name cannot contain control characters.';
  }
  return null;
}

export function validateUbuntuHostPath(path: string): string | null {
  if (!path.startsWith('/')) {
    return 'Enter an absolute Ubuntu path beginning with /.';
  }
  if (path.length > 1_024) {
    return 'The host path cannot exceed 1,024 characters.';
  }
  if (path.includes('\\')) {
    return 'Ubuntu host paths cannot contain backslashes.';
  }
  if ([...path].some(isControl)) {
    return 'The host path cannot contain control characters.';
  }
  const normalized = path.length > 1 ? path.replace(/\/+$/, '') : path;
  if (normalized === '/' || normalized === '/home' || normalized === '/srv' || normalized === '/mnt') {
    return 'Choose a specific folder below /home, /srv, or /mnt.';
  }
  const protectedRoots = ['/proc', '/sys', '/dev', '/run', '/var/run'];
  if (protectedRoots.some((root) => normalized === root || normalized.startsWith(`${root}/`))) {
    return 'Choose a folder outside protected system folders such as /proc, /sys, /dev, and /run.';
  }
  return null;
}

function isControl(character: string): boolean {
  const code = character.codePointAt(0) ?? 0;
  return code <= 0x1f || (code >= 0x7f && code <= 0x9f);
}
