import { A11yModule } from '@angular/cdk/a11y';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  HostListener,
  ViewChild,
  inject,
  signal,
} from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { AuthenticationStore } from '../../core/auth/authentication-store';

@Component({
  selector: 'app-account-menu',
  imports: [A11yModule, ReactiveFormsModule],
  templateUrl: './account-menu.component.html',
  styleUrl: './account-menu.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AccountMenuComponent {
  readonly auth = inject(AuthenticationStore);
  readonly menuOpen = signal(false);
  readonly passwordDialogOpen = signal(false);
  readonly showPasswords = signal(false);
  readonly localError = signal<string | null>(null);
  readonly announcement = signal<string | null>(null);

  private readonly forms = inject(FormBuilder);

  readonly passwordForm = this.forms.nonNullable.group({
    currentPassword: [
      '',
      [Validators.required, Validators.minLength(12), Validators.maxLength(128)],
    ],
    newPassword: ['', [Validators.required, Validators.minLength(12), Validators.maxLength(128)]],
    confirmPassword: [
      '',
      [Validators.required, Validators.minLength(12), Validators.maxLength(128)],
    ],
  });

  @ViewChild('accountTrigger', { read: ElementRef })
  private accountTrigger?: ElementRef<HTMLButtonElement>;

  constructor() {
    inject(DestroyRef).onDestroy(() => this.clearSensitiveFields());
  }

  toggleMenu(): void {
    if (this.auth.state().pending) {
      return;
    }

    this.menuOpen.update((open) => !open);
  }

  openPasswordDialog(): void {
    if (this.auth.state().pending) {
      return;
    }

    this.menuOpen.set(false);
    this.localError.set(null);
    this.passwordDialogOpen.set(true);
  }

  closePasswordDialog(): void {
    if (!this.passwordDialogOpen() || this.auth.state().pending) {
      return;
    }

    this.passwordDialogOpen.set(false);
    this.clearSensitiveFields();
    queueMicrotask(() => this.accountTrigger?.nativeElement.focus());
  }

  async submitPasswordChange(): Promise<void> {
    this.localError.set(null);
    if (this.auth.state().pending) {
      return;
    }

    const values = this.passwordForm.getRawValue();
    if (this.passwordForm.invalid) {
      this.passwordForm.markAllAsTouched();
      this.localError.set('Enter passwords containing between 12 and 128 characters.');
      return;
    }

    if (values.newPassword !== values.confirmPassword) {
      this.localError.set('New passwords do not match.');
      return;
    }

    await this.auth.changePassword({
      currentPassword: values.currentPassword,
      newPassword: values.newPassword,
    });
    const state = this.auth.state();
    if (state.phase === 'authenticated' && state.errorCode === null) {
      this.passwordDialogOpen.set(false);
      this.clearSensitiveFields();
      this.announcement.set('Password changed. Other sessions were signed out.');
      queueMicrotask(() => this.accountTrigger?.nativeElement.focus());
    }
  }

  logout(): void {
    if (this.auth.state().pending) {
      return;
    }

    this.menuOpen.set(false);
    void this.auth.logout();
  }

  @HostListener('document:keydown.escape')
  handleEscape(): void {
    if (this.passwordDialogOpen()) {
      this.closePasswordDialog();
      return;
    }

    if (this.menuOpen()) {
      this.menuOpen.set(false);
      queueMicrotask(() => this.accountTrigger?.nativeElement.focus());
    }
  }

  private clearSensitiveFields(): void {
    this.passwordForm.reset();
    this.showPasswords.set(false);
    this.localError.set(null);
  }
}
