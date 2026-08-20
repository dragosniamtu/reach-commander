import { DOCUMENT } from '@angular/common';
import { Inject, Injectable } from '@angular/core';
import { Subject } from 'rxjs';
import { CommanderCommand, CommanderFunctionKey } from './commander-command';

@Injectable({ providedIn: 'root' })
export class CommanderKeyboardService {
  private readonly commandSubject = new Subject<CommanderCommand>();
  private listening = false;

  readonly commands = this.commandSubject.asObservable();

  constructor(@Inject(DOCUMENT) private readonly document: Document) {}

  start(): void {
    if (this.listening) {
      return;
    }

    this.document.addEventListener('keydown', this.handleKeydown);
    this.listening = true;
  }

  stop(): void {
    if (!this.listening) {
      return;
    }

    this.document.removeEventListener('keydown', this.handleKeydown);
    this.listening = false;
  }

  private readonly handleKeydown = (event: KeyboardEvent): void => {
    const command = mapKeyboardEvent(event);
    if (!command) {
      return;
    }

    event.preventDefault();
    this.commandSubject.next(command);
  };
}

export function mapKeyboardEvent(event: KeyboardEvent): CommanderCommand | null {
  if (isTextControl(event.target)) {
    return event.key === 'Escape' ? { type: 'escape' } : null;
  }

  if (event.ctrlKey && !event.altKey && !event.metaKey) {
    switch (event.key.toLocaleLowerCase()) {
      case 'a':
        return { type: 'select-all' };
      case 'f':
        return { type: 'focus-search' };
      case 'l':
        return { type: 'focus-path' };
      case 'm':
        return { type: 'multi-rename' };
      case 'r':
        return { type: 'refresh' };
      case 't':
        return { type: 'new-tab' };
      case 'w':
        return { type: 'close-tab' };
      default:
        return null;
    }
  }

  if (event.altKey || event.metaKey || event.ctrlKey) {
    return null;
  }

  switch (event.key) {
    case 'ArrowUp':
      return { type: 'move-cursor', amount: -1 };
    case 'ArrowDown':
      return { type: 'move-cursor', amount: 1 };
    case 'PageUp':
      return { type: 'move-page', direction: -1 };
    case 'PageDown':
      return { type: 'move-page', direction: 1 };
    case 'Home':
      return { type: 'move-boundary', boundary: 'home' };
    case 'End':
      return { type: 'move-boundary', boundary: 'end' };
    case 'Enter':
      return { type: 'open-cursor' };
    case 'Backspace':
      return { type: 'backspace' };
    case 'Tab':
      return { type: 'switch-panel' };
    case 'Insert':
      return { type: 'toggle-selection' };
    case 'Escape':
      return { type: 'escape' };
    case 'F3':
    case 'F4':
    case 'F5':
    case 'F6':
    case 'F7':
    case 'F8':
    case 'F9':
      return { type: 'function-key', key: event.key as CommanderFunctionKey };
    default:
      return event.key.length === 1 ? { type: 'filter-text', text: event.key } : null;
  }
}

function isTextControl(target: EventTarget | null): boolean {
  return (
    target instanceof HTMLInputElement ||
    target instanceof HTMLTextAreaElement ||
    target instanceof HTMLSelectElement ||
    (target instanceof HTMLElement && target.isContentEditable)
  );
}
