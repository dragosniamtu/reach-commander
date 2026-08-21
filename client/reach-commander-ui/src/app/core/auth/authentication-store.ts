import { HttpErrorResponse } from '@angular/common/http';
import { Injectable, signal } from '@angular/core';
import { AuthenticationApi } from './authentication-api';
import { AuthenticationChannel } from './authentication-channel';
import { ProtectedStateResetService } from './protected-state-reset.service';
import {
  AuthenticationSessionDto,
  AuthenticationViewState,
  ChangePasswordCommand,
  LoginCommand,
  SetupCommand,
} from './authentication.models';

const initialState: AuthenticationViewState = {
  phase: 'checking',
  username: null,
  pending: false,
  errorCode: null,
  errorMessage: null,
};

@Injectable({ providedIn: 'root' })
export class AuthenticationStore {
  private readonly mutableState = signal<AuthenticationViewState>(initialState);
  private initializationInFlight: Promise<void> | null = null;
  private operationInFlight: Promise<void> | null = null;
  private antiforgeryInFlight: Promise<void> | null = null;

  readonly state = this.mutableState.asReadonly();

  constructor(
    private readonly api: AuthenticationApi,
    private readonly channel: AuthenticationChannel,
    private readonly protectedState: ProtectedStateResetService,
  ) {
    this.channel.unauthorized$.subscribe(() => this.lock());
  }

  initialize(): Promise<void> {
    if (this.initializationInFlight) {
      return this.initializationInFlight;
    }

    const operation = this.runInitialization();
    this.initializationInFlight = operation;
    void operation.finally(() => {
      if (this.initializationInFlight === operation) {
        this.initializationInFlight = null;
      }
    });
    return operation;
  }

  retry(): Promise<void> {
    return this.initialize();
  }

  setup(command: SetupCommand): Promise<void> {
    return this.signIn('setupRequired', () => this.api.setup(command));
  }

  login(command: LoginCommand): Promise<void> {
    return this.signIn('anonymous', () => this.api.login(command));
  }

  logout(): Promise<void> {
    return this.runExclusive(async () => {
      this.mutableState.update((state) => ({
        ...state,
        pending: true,
        errorCode: null,
        errorMessage: null,
      }));
      try {
        await this.ensureAntiforgeryToken();
        await this.api.logout();
      } catch {
        // Local logout is deliberately fail-closed even when the network is unavailable.
      } finally {
        this.lock();
      }
    });
  }

  changePassword(command: ChangePasswordCommand): Promise<void> {
    return this.runExclusive(async () => {
      const current = this.state();
      if (current.phase !== 'authenticated' || !current.username) {
        this.lock();
        return;
      }

      this.mutableState.set({
        ...current,
        pending: true,
        errorCode: null,
        errorMessage: null,
      });
      try {
        await this.ensureAntiforgeryToken();
        const session = requireAuthenticated(await this.api.changePassword(command));
        this.channel.clearAntiforgeryToken();
        try {
          await this.ensureAntiforgeryToken();
          this.mutableState.set(viewState(session));
        } catch {
          this.mutableState.set({
            ...viewState(session),
            errorCode: 'request_failed',
            errorMessage: 'The password changed, but the security token could not be refreshed.',
          });
        }
      } catch (error: unknown) {
        if (this.state().phase !== 'authenticated') {
          return;
        }

        this.mutableState.set({
          phase: 'authenticated',
          username: current.username,
          pending: false,
          ...safeError(error),
        });
      }
    });
  }

  lock(): void {
    this.channel.clearAntiforgeryToken();
    this.protectedState.reset();
    this.mutableState.set({
      phase: 'anonymous',
      username: null,
      pending: false,
      errorCode: null,
      errorMessage: null,
    });
  }

  private async runInitialization(): Promise<void> {
    this.mutableState.set({ ...initialState, pending: true });
    try {
      await this.ensureAntiforgeryToken();
      this.mutableState.set(viewState(await this.api.getSession()));
    } catch {
      this.channel.clearAntiforgeryToken();
      this.mutableState.set({
        phase: 'unavailable',
        username: null,
        pending: false,
        errorCode: 'request_failed',
        errorMessage: 'Authentication is temporarily unavailable.',
      });
    }
  }

  private signIn(
    expectedPhase: 'setupRequired' | 'anonymous',
    submit: () => Promise<AuthenticationSessionDto>,
  ): Promise<void> {
    return this.runExclusive(async () => {
      this.mutableState.set({
        phase: expectedPhase,
        username: null,
        pending: true,
        errorCode: null,
        errorMessage: null,
      });
      let signedIn = false;
      try {
        await this.ensureAntiforgeryToken();
        const session = requireAuthenticated(await submit());
        signedIn = true;
        this.channel.clearAntiforgeryToken();
        await this.ensureAntiforgeryToken();
        this.mutableState.set(viewState(session));
      } catch (error: unknown) {
        if (signedIn) {
          this.channel.clearAntiforgeryToken();
          this.mutableState.set({
            phase: 'unavailable',
            username: null,
            pending: false,
            errorCode: 'request_failed',
            errorMessage: 'Authentication is temporarily unavailable.',
          });
          return;
        }

        if (this.state().phase !== expectedPhase) {
          return;
        }

        this.mutableState.set({
          phase: expectedPhase,
          username: null,
          pending: false,
          ...safeError(error),
        });
      }
    });
  }

  private runExclusive(operationFactory: () => Promise<void>): Promise<void> {
    if (this.operationInFlight) {
      return this.operationInFlight;
    }

    const operation = operationFactory();
    this.operationInFlight = operation;
    void operation.finally(() => {
      if (this.operationInFlight === operation) {
        this.operationInFlight = null;
      }
    });
    return operation;
  }

  private ensureAntiforgeryToken(): Promise<void> {
    if (this.channel.token()) {
      return Promise.resolve();
    }

    if (this.antiforgeryInFlight) {
      return this.antiforgeryInFlight;
    }

    const request = this.api.getAntiforgeryToken().then((token) => {
      if (!token) {
        throw new Error('The antiforgery response did not contain a token.');
      }

      this.channel.setAntiforgeryToken(token);
    });
    this.antiforgeryInFlight = request;
    const clearRequest = (): void => {
      if (this.antiforgeryInFlight === request) {
        this.antiforgeryInFlight = null;
      }
    };
    void request.then(clearRequest, clearRequest);
    return request;
  }
}

function viewState(session: AuthenticationSessionDto): AuthenticationViewState {
  if (session.state === 'authenticated') {
    if (!session.username) {
      throw new Error('The authenticated session did not include a username.');
    }

    return {
      phase: 'authenticated',
      username: session.username,
      pending: false,
      errorCode: null,
      errorMessage: null,
    };
  }

  return {
    phase: session.state,
    username: null,
    pending: false,
    errorCode: null,
    errorMessage: null,
  };
}

function requireAuthenticated(session: AuthenticationSessionDto): AuthenticationSessionDto {
  if (session.state !== 'authenticated' || !session.username) {
    throw new Error('The authentication response was invalid.');
  }

  return session;
}

function safeError(error: unknown): Pick<AuthenticationViewState, 'errorCode' | 'errorMessage'> {
  const code = problemCode(error);
  switch (code) {
    case 'invalid_credentials':
      return {
        errorCode: code,
        errorMessage: 'The supplied credentials are not valid.',
      };
    case 'setup_failed':
      return {
        errorCode: code,
        errorMessage: 'Account setup could not be completed.',
      };
    case 'invalid_username':
      return {
        errorCode: code,
        errorMessage: 'Enter a username containing between 3 and 64 characters.',
      };
    case 'invalid_password':
      return {
        errorCode: code,
        errorMessage: 'Enter a password containing between 12 and 128 characters.',
      };
    case 'administrator_exists':
      return {
        errorCode: code,
        errorMessage: 'The administrator account has already been configured.',
      };
    default:
      return {
        errorCode: 'request_failed',
        errorMessage: 'The request could not be completed.',
      };
  }
}

function problemCode(error: unknown): string | null {
  if (
    !(error instanceof HttpErrorResponse) ||
    typeof error.error !== 'object' ||
    error.error === null ||
    !('code' in error.error) ||
    typeof error.error.code !== 'string'
  ) {
    return null;
  }

  return error.error.code;
}
