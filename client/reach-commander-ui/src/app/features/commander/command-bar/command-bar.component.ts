import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { CommanderFunctionKey } from '../../../core/keyboard/commander-command';

interface CommandAction {
  readonly key: CommanderFunctionKey;
  readonly label: string;
  readonly enabled: boolean;
  readonly reason: string | null;
}

export interface FileCommandAvailability {
  readonly copy: { readonly enabled: boolean; readonly reason: string | null; readonly label: 'Copy' | 'Extract' };
  readonly move: { readonly enabled: boolean; readonly reason: string | null };
  readonly createDirectory: { readonly enabled: boolean; readonly reason: string | null };
  readonly delete: { readonly enabled: boolean; readonly reason: string | null };
}

const unavailableCommands: FileCommandAvailability = {
  copy: { enabled: false, reason: 'Select or focus an item.', label: 'Copy' },
  move: { enabled: false, reason: 'Select or focus an item.' },
  createDirectory: { enabled: false, reason: 'Choose a writable filesystem folder.' },
  delete: { enabled: false, reason: 'Select or focus an item.' },
};

@Component({
  selector: 'app-command-bar',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './command-bar.component.html',
  styleUrl: './command-bar.component.scss',
})
export class CommandBarComponent {
  readonly commandSelected = output<CommanderFunctionKey>();
  readonly availability = input<FileCommandAvailability>(unavailableCommands);
  readonly actions = computed<readonly CommandAction[]>(() => [
    { key: 'F3', label: 'View', enabled: false, reason: 'File viewing arrives in a later milestone.' },
    { key: 'F4', label: 'Rename', enabled: false, reason: 'Use Multi-Rename from the toolbar or Ctrl+M.' },
    { key: 'F5', label: this.availability().copy.label, enabled: this.availability().copy.enabled, reason: this.availability().copy.reason },
    { key: 'F6', label: 'Move', enabled: this.availability().move.enabled, reason: this.availability().move.reason },
    { key: 'F7', label: 'MkDir', enabled: this.availability().createDirectory.enabled, reason: this.availability().createDirectory.reason },
    { key: 'F8', label: 'Delete', enabled: this.availability().delete.enabled, reason: this.availability().delete.reason },
    { key: 'F9', label: 'Menu', enabled: true, reason: null },
  ]);
}
