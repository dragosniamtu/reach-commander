import { Injectable } from '@angular/core';

export type ProtectedStateResetHandler = () => void;

@Injectable({ providedIn: 'root' })
export class ProtectedStateResetService {
  private handler: ProtectedStateResetHandler | null = null;

  register(handler: ProtectedStateResetHandler): () => void {
    this.handler = handler;
    return () => {
      if (this.handler === handler) {
        this.handler = null;
      }
    };
  }

  reset(): void {
    this.handler?.();
  }
}
