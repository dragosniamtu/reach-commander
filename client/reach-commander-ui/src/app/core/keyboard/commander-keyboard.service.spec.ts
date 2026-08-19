import { TestBed } from '@angular/core/testing';
import { CommanderCommand } from './commander-command';
import { CommanderKeyboardService } from './commander-keyboard.service';

describe('CommanderKeyboardService', () => {
  let service: CommanderKeyboardService;
  let commands: CommanderCommand[];

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [CommanderKeyboardService] });
    service = TestBed.inject(CommanderKeyboardService);
    commands = [];
    service.commands.subscribe((command) => commands.push(command));
    service.start();
  });

  afterEach(() => service.stop());

  it.each([
    ['ArrowUp', {}, { type: 'move-cursor', amount: -1 }],
    ['ArrowDown', {}, { type: 'move-cursor', amount: 1 }],
    ['PageUp', {}, { type: 'move-page', direction: -1 }],
    ['PageDown', {}, { type: 'move-page', direction: 1 }],
    ['Home', {}, { type: 'move-boundary', boundary: 'home' }],
    ['End', {}, { type: 'move-boundary', boundary: 'end' }],
    ['Enter', {}, { type: 'open-cursor' }],
    ['Backspace', {}, { type: 'backspace' }],
    ['Tab', {}, { type: 'switch-panel' }],
    ['Insert', {}, { type: 'toggle-selection' }],
    ['Escape', {}, { type: 'escape' }],
    ['a', { ctrlKey: true }, { type: 'select-all' }],
    ['l', { ctrlKey: true }, { type: 'focus-path' }],
    ['r', { ctrlKey: true }, { type: 'refresh' }],
    ['t', { ctrlKey: true }, { type: 'new-tab' }],
    ['w', { ctrlKey: true }, { type: 'close-tab' }],
    ['F5', {}, { type: 'function-key', key: 'F5' }],
    ['x', {}, { type: 'filter-text', text: 'x' }],
  ] as const)('maps %s to a semantic command', (key, init, expected) => {
    const event = keyEvent(key, init);

    document.dispatchEvent(event);

    expect(commands.at(-1)).toEqual(expected);
    expect(event.defaultPrevented).toBe(true);
  });

  it('preserves ordinary text editing and handles only Escape in inputs', () => {
    const input = document.createElement('input');
    document.body.append(input);
    const letter = keyEvent('a');
    const escape = keyEvent('Escape');

    input.dispatchEvent(letter);
    input.dispatchEvent(escape);

    expect(letter.defaultPrevented).toBe(false);
    expect(commands).toEqual([{ type: 'escape' }]);
    input.remove();
  });

  it('does not dispatch after stop and start is idempotent', () => {
    service.start();
    document.dispatchEvent(keyEvent('ArrowDown'));
    service.stop();
    document.dispatchEvent(keyEvent('ArrowDown'));

    expect(commands).toEqual([{ type: 'move-cursor', amount: 1 }]);
  });
});

function keyEvent(key: string, init: KeyboardEventInit = {}): KeyboardEvent {
  return new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true, ...init });
}
