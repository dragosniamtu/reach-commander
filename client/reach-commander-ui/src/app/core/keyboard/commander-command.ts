export type CommanderFunctionKey = 'F3' | 'F4' | 'F5' | 'F6' | 'F7' | 'F8' | 'F9';

export type CommanderCommand =
  | { readonly type: 'move-cursor'; readonly amount: number }
  | { readonly type: 'move-page'; readonly direction: -1 | 1 }
  | { readonly type: 'move-boundary'; readonly boundary: 'home' | 'end' }
  | { readonly type: 'open-cursor' }
  | { readonly type: 'backspace' }
  | { readonly type: 'switch-panel' }
  | { readonly type: 'toggle-selection' }
  | { readonly type: 'select-all' }
  | { readonly type: 'multi-rename' }
  | { readonly type: 'escape' }
  | { readonly type: 'focus-path' }
  | { readonly type: 'refresh' }
  | { readonly type: 'new-tab' }
  | { readonly type: 'close-tab' }
  | { readonly type: 'filter-text'; readonly text: string }
  | { readonly type: 'function-key'; readonly key: CommanderFunctionKey };
