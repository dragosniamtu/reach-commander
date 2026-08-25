import { A11yModule } from '@angular/cdk/a11y';
import {
  AfterViewInit,
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  HostListener,
  ViewChild,
  inject,
  signal,
} from '@angular/core';
import { FileOperationConflictDecision } from '../../../core/api/api.models';
import { FileSizePipe } from '../../../shared/pipes/file-size.pipe';
import { FileOperationStore } from './file-operation.store';

@Component({
  selector: 'app-copy-move-dialog',
  imports: [A11yModule, FileSizePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './copy-move-dialog.component.html',
  styleUrl: './copy-move-dialog.component.scss',
})
export class CopyMoveDialogComponent implements AfterViewInit {
  readonly store = inject(FileOperationStore);
  readonly lastDecision = signal<FileOperationConflictDecision | null>(null);

  @ViewChild('destinationInput', { read: ElementRef })
  private destinationInput?: ElementRef<HTMLInputElement>;

  ngAfterViewInit(): void {
    this.destinationInput?.nativeElement.focus({ preventScroll: true });
  }

  destinationChanged(event: Event): void {
    void this.store.setDestination((event.target as HTMLInputElement).value);
  }

  decisionChanged(conflictId: string, event: Event): void {
    const value = (event.target as HTMLSelectElement).value;
    if (!isConflictDecision(value)) {
      return;
    }
    this.lastDecision.set(value);
    this.store.setConflictDecision(conflictId, value);
  }

  applyRemainingChanged(event: Event): void {
    if (!(event.target as HTMLInputElement).checked) {
      return;
    }
    const decision = this.lastDecision();
    if (decision) {
      this.store.applyDecisionToRemaining(decision);
    }
  }

  start(): void {
    void this.store.submit();
  }

  cancel(): void {
    this.store.closeConfirmation();
  }

  basename(path: string): string {
    return path.split('/').at(-1) || path;
  }

  operationLabel(): string {
    return this.store.context()?.kind === 'move' ? 'Move' : 'Copy';
  }

  @HostListener('keydown', ['$event'])
  handleKeydown(event: KeyboardEvent): void {
    event.stopPropagation();
    if (event.key === 'Escape') {
      event.preventDefault();
      this.cancel();
    }
  }
}

function isConflictDecision(value: string): value is FileOperationConflictDecision {
  return value === 'overwrite' || value === 'skip' || value === 'createUniqueName';
}
