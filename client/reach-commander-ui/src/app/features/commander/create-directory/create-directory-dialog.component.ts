import { A11yModule } from '@angular/cdk/a11y';
import { ChangeDetectionStrategy, Component, HostListener, computed, inject, input, output, signal } from '@angular/core';
import { CommanderApiPort, FileEntryDto } from '../../../core/api/api.models';

@Component({
  selector: 'app-create-directory-dialog',
  imports: [A11yModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './create-directory-dialog.component.html',
  styleUrl: './create-directory-dialog.component.scss',
})
export class CreateDirectoryDialogComponent {
  private readonly api = inject(CommanderApiPort);
  readonly sourceId = input.required<string>();
  readonly sourceName = input.required<string>();
  readonly parentLogicalPath = input.required<string>();
  readonly created = output<FileEntryDto>();
  readonly closeRequested = output<void>();
  readonly name = signal('');
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly validationError = computed(() => validateDirectoryName(this.name()));
  readonly canSubmit = computed(() => !this.busy() && this.validationError() === null);

  setName(name: string): void {
    this.name.set(name);
    this.error.set(null);
  }

  nameChanged(event: Event): void {
    this.setName((event.target as HTMLInputElement).value);
  }

  async submit(): Promise<void> {
    if (!this.canSubmit()) return;
    this.busy.set(true);
    this.error.set(null);
    try {
      const entry = await this.api.createDirectory({
        sourceId: this.sourceId(),
        parentLogicalPath: this.parentLogicalPath(),
        name: this.name(),
      });
      this.created.emit(entry);
    } catch (error: unknown) {
      this.error.set(safeError(error));
    } finally {
      this.busy.set(false);
    }
  }

  requestClose(): void {
    if (!this.busy()) this.closeRequested.emit();
  }

  @HostListener('keydown', ['$event'])
  handleKeydown(event: KeyboardEvent): void {
    event.stopPropagation();
    if (event.key === 'Escape') {
      event.preventDefault();
      this.requestClose();
    } else if (event.key === 'Enter') {
      event.preventDefault();
      void this.submit();
    }
  }
}

export function validateDirectoryName(name: string): string | null {
  if (name.length === 0) return 'Enter a directory name.';
  if (name === '.' || name === '..') return "A directory cannot be named '.' or '..'.";
  if (/[\\/]/.test(name)) return 'Directory names cannot contain path separators.';
  if (/[<>:"|?*]/.test(name) || [...name].some((character) => isControl(character))) {
    return 'The directory name contains a forbidden character.';
  }
  if (name.endsWith('.') || name.endsWith(' ')) return 'A directory name cannot end with a dot or space.';
  if (/^(con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\.|$)/i.test(name)) return 'This name is reserved by Windows.';
  if (/^\.reachcommander-trash$/i.test(name) || /^\.reachcommander-operation-/i.test(name)) {
    return 'This name is reserved by ReachCommander.';
  }
  if (new TextEncoder().encode(name).length > 255) return 'The directory name exceeds the 255-byte limit.';
  return null;
}

function isControl(character: string): boolean {
  const code = character.codePointAt(0) ?? 0;
  return code <= 0x1f || (code >= 0x7f && code <= 0x9f);
}

function safeError(error: unknown): string {
  if (typeof error === 'object' && error !== null) {
    const candidate = error as Record<string, unknown>;
    const body = typeof candidate['error'] === 'object' && candidate['error'] !== null
      ? candidate['error'] as Record<string, unknown> : candidate;
    if (typeof body['detail'] === 'string') return body['detail'];
  }
  return 'The directory could not be created.';
}
