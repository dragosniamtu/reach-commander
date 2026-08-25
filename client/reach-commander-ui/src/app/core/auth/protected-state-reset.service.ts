import { Injectable } from '@angular/core';

export type ProtectedStateResetHandler = () => void;

@Injectable({ providedIn: 'root' })
export class ProtectedStateResetService {
  private readonly handlers = new Set<ProtectedStateResetHandler>();

  register(handler: ProtectedStateResetHandler): () => void {
    this.handlers.add(handler);
    return () => {
      this.handlers.delete(handler);
    };
  }

  reset(): void {
    for (const handler of [...this.handlers]) {
      handler();
    }
  }
}
