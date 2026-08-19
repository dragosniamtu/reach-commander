import { ChangeDetectionStrategy, Component } from '@angular/core';
import { CommanderShellComponent } from './features/commander/commander-shell/commander-shell.component';

@Component({
  selector: 'app-root',
  imports: [CommanderShellComponent],
  templateUrl: './app.html',
  styleUrl: './app.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class App {}
