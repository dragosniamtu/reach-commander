import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import {
  AbstractControl,
  FormBuilder,
  ReactiveFormsModule,
  ValidationErrors,
  Validators,
} from '@angular/forms';
import { AuthenticationStore } from '../../core/auth/authentication-store';

@Component({
  selector: 'app-authentication-screen',
  imports: [ReactiveFormsModule],
  templateUrl: './authentication-screen.component.html',
  styleUrl: './authentication-screen.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AuthenticationScreenComponent {
  readonly auth = inject(AuthenticationStore);
  readonly showLoginPassword = signal(false);
  readonly showSetupPassword = signal(false);
  readonly localError = signal<string | null>(null);

  private readonly forms = inject(FormBuilder);

  readonly loginForm = this.forms.nonNullable.group({
    username: ['', [Validators.required, Validators.minLength(3), Validators.maxLength(64)]],
    password: ['', [Validators.required, Validators.minLength(12), Validators.maxLength(128)]],
  });

  readonly setupForm = this.forms.nonNullable.group(
    {
      setupCode: ['', [Validators.required, Validators.maxLength(256)]],
      username: ['', [Validators.required, Validators.minLength(3), Validators.maxLength(64)]],
      password: ['', [Validators.required, Validators.minLength(12), Validators.maxLength(128)]],
      passwordConfirmation: [
        '',
        [Validators.required, Validators.minLength(12), Validators.maxLength(128)],
      ],
    },
    { validators: matchingPasswords },
  );

  async submitLogin(): Promise<void> {
    this.localError.set(null);
    if (this.auth.state().pending) {
      return;
    }

    if (this.loginForm.invalid) {
      this.loginForm.markAllAsTouched();
      this.localError.set('Enter a valid username and password.');
      return;
    }

    const command = this.loginForm.getRawValue();
    await this.auth.login(command);
    if (this.auth.state().phase === 'authenticated') {
      this.loginForm.reset();
    } else {
      this.loginForm.controls.password.reset();
    }
  }

  async submitSetup(): Promise<void> {
    this.localError.set(null);
    if (this.auth.state().pending) {
      return;
    }

    if (this.setupForm.invalid) {
      this.setupForm.markAllAsTouched();
      this.localError.set(
        this.setupForm.hasError('passwordMismatch')
          ? 'Passwords must match.'
          : 'Complete every field using the required lengths.',
      );
      return;
    }

    const value = this.setupForm.getRawValue();
    await this.auth.setup({
      setupCode: value.setupCode,
      username: value.username,
      password: value.password,
    });
    if (this.auth.state().phase === 'authenticated') {
      this.setupForm.reset();
    } else {
      this.setupForm.patchValue({
        setupCode: '',
        password: '',
        passwordConfirmation: '',
      });
    }
  }

  retry(): void {
    this.localError.set(null);
    void this.auth.retry();
  }
}

function matchingPasswords(control: AbstractControl): ValidationErrors | null {
  const password = control.get('password')?.value;
  const confirmation = control.get('passwordConfirmation')?.value;
  return password === confirmation ? null : { passwordMismatch: true };
}
