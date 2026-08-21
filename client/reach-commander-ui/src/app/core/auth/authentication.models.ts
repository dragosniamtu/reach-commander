export type SessionState = 'setupRequired' | 'anonymous' | 'authenticated';

export interface AuthenticationSessionDto {
  readonly state: SessionState;
  readonly username: string | null;
}

export interface SetupCommand {
  readonly setupCode: string;
  readonly username: string;
  readonly password: string;
}

export interface LoginCommand {
  readonly username: string;
  readonly password: string;
}

export interface ChangePasswordCommand {
  readonly currentPassword: string;
  readonly newPassword: string;
}

export type AuthenticationPhase = 'checking' | SessionState | 'unavailable';

export interface AuthenticationViewState {
  readonly phase: AuthenticationPhase;
  readonly username: string | null;
  readonly pending: boolean;
  readonly errorCode: string | null;
  readonly errorMessage: string | null;
}
