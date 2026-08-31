import { ComponentFixture, TestBed } from '@angular/core/testing';
import {
  ActivePanelToolbarComponent,
  ActivePanelToolbarContext,
} from './active-panel-toolbar.component';

describe('ActivePanelToolbarComponent', () => {
  let fixture: ComponentFixture<ActivePanelToolbarComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [ActivePanelToolbarComponent],
    }).compileComponents();
    fixture = TestBed.createComponent(ActivePanelToolbarComponent);
    setInputs(context(), '');
    fixture.componentRef.setInput('sourceManagementSupported', true);
    fixture.componentRef.setInput('sourceManagementPending', false);
    fixture.componentRef.setInput('sourceManagementDisabledReason', null);
    fixture.componentRef.setInput('sourceManagementRetryAvailable', false);
  });

  it('shows the active context and accessible logical path', () => {
    fixture.detectChanges();
    const chip = fixture.nativeElement.querySelector('[data-testid="active-panel-context"]');

    expect(chip.textContent).toContain('LEFT · Media');
    expect(chip.getAttribute('aria-label')).toContain('/incoming');
  });

  it('enables actions only for an available writable source with rename targets', () => {
    fixture.detectChanges();
    expect(button('toolbar-multi-rename').disabled).toBe(false);
    expect(button('toolbar-add-files').disabled).toBe(false);

    setInputs(context({ readOnly: true }), '');
    fixture.detectChanges();
    expect(button('toolbar-multi-rename').disabled).toBe(true);
    expect(button('toolbar-add-files').disabled).toBe(true);
    expect(button('toolbar-add-files').closest('[role="group"]')?.getAttribute('title')).toContain(
      'read-only',
    );

    setInputs(context({ hasRenameTargets: false }), '');
    fixture.detectChanges();
    expect(button('toolbar-multi-rename').disabled).toBe(true);
    expect(button('toolbar-add-files').disabled).toBe(false);

    setInputs(context({ archive: true }), '');
    fixture.detectChanges();
    expect(button('toolbar-multi-rename').disabled).toBe(true);
    expect(button('toolbar-add-files').disabled).toBe(true);
    expect(button('toolbar-add-files').closest('[role="group"]')?.getAttribute('title')).toContain(
      'archive',
    );
  });

  it('emits search input and clear while describing wildcard support', () => {
    const changed = vi.fn();
    fixture.componentInstance.filterChanged.subscribe(changed);
    setInputs(context(), '*.exe');
    fixture.detectChanges();
    const search = fixture.nativeElement.querySelector('input[type="search"]') as HTMLInputElement;

    expect(search.getAttribute('aria-label')).toBe('Search active panel');
    expect(search.getAttribute('aria-describedby')).toBeTruthy();
    search.value = 'report-??.pdf';
    search.dispatchEvent(new Event('input'));
    button('toolbar-clear-search').click();

    expect(changed).toHaveBeenNthCalledWith(1, 'report-??.pdf');
    expect(changed).toHaveBeenNthCalledWith(2, '');
  });

  it('copies selected files, clears the input, and ignores an empty selection', () => {
    const selected = vi.fn();
    fixture.componentInstance.filesSelected.subscribe(selected);
    fixture.detectChanges();
    const input = fixture.nativeElement.querySelector('input[type="file"]') as HTMLInputElement;
    const files = [new File(['one'], 'one.txt'), new File(['two'], 'two.bin')];
    Object.defineProperty(input, 'files', { configurable: true, value: files });

    input.dispatchEvent(new Event('change'));

    expect(selected).toHaveBeenCalledOnce();
    expect(selected.mock.calls[0]![0]).toEqual(files);
    expect(selected.mock.calls[0]![0]).not.toBe(files);
    expect(input.value).toBe('');
    expect(input.multiple).toBe(true);
    expect(input.hasAttribute('accept')).toBe(false);

    Object.defineProperty(input, 'files', { configurable: true, value: [] });
    input.dispatchEvent(new Event('change'));
    expect(selected).toHaveBeenCalledOnce();
  });

  it('keeps local icons hidden while actions retain their text', () => {
    fixture.detectChanges();
    const rename = button('toolbar-multi-rename');

    expect(rename.textContent).toContain('Multi-Rename');
    expect(rename.querySelector('svg')?.getAttribute('aria-hidden')).toBe('true');
  });

  it('exposes the same extraction action and disabled reason as F5', () => {
    const requested = vi.fn();
    fixture.componentInstance.extractRequested.subscribe(requested);
    setInputs(context({ extractAvailable: true, extractDisabledReason: null }), '');
    fixture.detectChanges();
    button('toolbar-extract').click();
    expect(requested).toHaveBeenCalledOnce();

    setInputs(context({
      extractAvailable: false,
      extractDisabledReason: 'Choose a writable destination.',
    }), '');
    fixture.detectChanges();
    expect(button('toolbar-extract').disabled).toBe(true);
    expect(button('toolbar-extract').closest('[role="group"]')?.getAttribute('title'))
      .toContain('writable destination');
  });

  it('opens managed Trash from the active-panel toolbar', () => {
    const requested = vi.fn();
    fixture.componentInstance.trashRequested.subscribe(requested);
    fixture.detectChanges();

    button('toolbar-trash').click();

    expect(requested).toHaveBeenCalledOnce();
  });

  it('offers one compact global Add source control with an unsupported tooltip', () => {
    const requested = vi.fn();
    fixture.componentInstance.sourceRequested.subscribe(requested);
    fixture.detectChanges();

    button('toolbar-add-source').click();
    expect(requested).toHaveBeenCalledOnce();

    fixture.componentRef.setInput('sourceManagementSupported', false);
    fixture.componentRef.setInput(
      'sourceManagementDisabledReason',
      'Rerun the latest Ubuntu installer once to add host source management.',
    );
    fixture.detectChanges();
    const addSource = button('toolbar-add-source');
    const wrapper = addSource.closest('[role="group"]');
    expect(addSource.disabled).toBe(true);
    expect(wrapper?.getAttribute('tabindex')).toBe('0');
    expect(wrapper?.getAttribute('title')).toContain('Rerun the latest Ubuntu installer once');
    expect(wrapper?.textContent).toContain('Add source');
  });

  it('uses the same compact control to retry transient capability discovery', () => {
    const sourceRequested = vi.fn();
    const retryRequested = vi.fn();
    fixture.componentInstance.sourceRequested.subscribe(sourceRequested);
    fixture.componentInstance.sourceCapabilityRetryRequested.subscribe(retryRequested);
    fixture.componentRef.setInput('sourceManagementSupported', false);
    fixture.componentRef.setInput('sourceManagementRetryAvailable', true);
    fixture.componentRef.setInput(
      'sourceManagementDisabledReason',
      'Source-management capability could not be loaded.',
    );
    fixture.detectChanges();

    const retry = button('toolbar-add-source');
    expect(retry.disabled).toBe(false);
    expect(retry.getAttribute('aria-label')).toBe('Retry source-management capability check');
    retry.click();

    expect(retryRequested).toHaveBeenCalledOnce();
    expect(sourceRequested).not.toHaveBeenCalled();
  });

  function setInputs(toolbarContext: ActivePanelToolbarContext, filter: string): void {
    fixture.componentRef.setInput('context', toolbarContext);
    fixture.componentRef.setInput('filter', filter);
  }

  function button(testId: string): HTMLButtonElement {
    return fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
  }
});

function context(overrides: Partial<ActivePanelToolbarContext> = {}): ActivePanelToolbarContext {
  return {
    side: 'left',
    sourceName: 'Media',
    logicalPath: '/incoming',
    available: true,
    readOnly: false,
    archive: false,
    hasRenameTargets: true,
    uploadPending: false,
    extractAvailable: false,
    extractDisabledReason: 'Select a supported archive to extract.',
    ...overrides,
  };
}
