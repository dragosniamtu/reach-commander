import { ChangeDetectionStrategy, Component, OnInit, inject } from '@angular/core';
import { AuthenticationStore } from './core/auth/authentication-store';
import { ThemeService } from './core/theme/theme.service';
import { AuthenticationScreenComponent } from './features/auth/authentication-screen.component';
import { CommanderShellComponent } from './features/commander/commander-shell/commander-shell.component';

@Component({
  selector: 'app-root',
  imports: [AuthenticationScreenComponent, CommanderShellComponent],
  templateUrl: './app.html',
  styleUrl: './app.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class App implements OnInit {
  readonly auth = inject(AuthenticationStore);
  readonly theme = inject(ThemeService);

  ngOnInit(): void {
    void this.auth.initialize();
  }
}
