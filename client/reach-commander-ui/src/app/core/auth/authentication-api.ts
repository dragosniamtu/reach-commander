import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import {
  AuthenticationSessionDto,
  ChangePasswordCommand,
  LoginCommand,
  SetupCommand,
} from './authentication.models';

@Injectable({ providedIn: 'root' })
export class AuthenticationApi {
  constructor(private readonly http: HttpClient) {}

  getSession(): Promise<AuthenticationSessionDto> {
    return firstValueFrom(this.http.get<AuthenticationSessionDto>('/api/auth/session'));
  }

  async getAntiforgeryToken(): Promise<string> {
    const response = await firstValueFrom(
      this.http.get<AntiforgeryTokenResponse>('/api/auth/antiforgery'),
    );
    return response.requestToken;
  }

  setup(command: SetupCommand): Promise<AuthenticationSessionDto> {
    return firstValueFrom(
      this.http.post<AuthenticationSessionDto>('/api/auth/setup', command),
    );
  }

  login(command: LoginCommand): Promise<AuthenticationSessionDto> {
    return firstValueFrom(
      this.http.post<AuthenticationSessionDto>('/api/auth/login', command),
    );
  }

  logout(): Promise<void> {
    return firstValueFrom(this.http.post<void>('/api/auth/logout', null));
  }

  changePassword(command: ChangePasswordCommand): Promise<AuthenticationSessionDto> {
    return firstValueFrom(
      this.http.post<AuthenticationSessionDto>('/api/auth/password', command),
    );
  }
}

interface AntiforgeryTokenResponse {
  readonly requestToken: string;
}
