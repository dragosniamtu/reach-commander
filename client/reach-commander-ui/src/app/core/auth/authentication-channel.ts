import { Injectable, signal } from '@angular/core';
import { Subject } from 'rxjs';

@Injectable({ providedIn: 'root' })
export class AuthenticationChannel {
  private readonly mutableToken = signal<string | null>(null);
  private readonly unauthorized = new Subject<void>();

  readonly token = this.mutableToken.asReadonly();
  readonly unauthorized$ = this.unauthorized.asObservable();

  setAntiforgeryToken(token: string): void {
    this.mutableToken.set(token);
  }

  clearAntiforgeryToken(): void {
    this.mutableToken.set(null);
  }

  notifyUnauthorized(): void {
    this.unauthorized.next();
  }
}
