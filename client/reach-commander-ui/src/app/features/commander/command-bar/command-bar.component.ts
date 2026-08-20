import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { CommanderFunctionKey } from '../../../core/keyboard/commander-command';

interface CommandAction {
  readonly key: CommanderFunctionKey;
  readonly label: string;
  readonly enabled: boolean;
}

@Component({
  selector: 'app-command-bar',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './command-bar.component.html',
  styleUrl: './command-bar.component.scss',
})
export class CommandBarComponent {
  readonly commandSelected = output<CommanderFunctionKey>();
  readonly extractEnabled = input(false);
  readonly actions = computed<readonly CommandAction[]>(() => [
    { key: 'F3', label: 'View', enabled: false },
    { key: 'F4', label: 'Rename', enabled: false },
    { key: 'F5', label: this.extractEnabled() ? 'Extract' : 'Copy', enabled: this.extractEnabled() },
    { key: 'F6', label: 'Move', enabled: false },
    { key: 'F7', label: 'MkDir', enabled: false },
    { key: 'F8', label: 'Delete', enabled: false },
    { key: 'F9', label: 'Menu', enabled: true },
  ]);
}
