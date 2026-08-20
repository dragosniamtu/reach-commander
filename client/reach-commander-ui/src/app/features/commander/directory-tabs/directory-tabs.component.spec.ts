import { ComponentFixture, TestBed } from '@angular/core/testing';
import { DirectoryTabsComponent } from './directory-tabs.component';

describe('DirectoryTabsComponent', () => {
  let fixture: ComponentFixture<DirectoryTabsComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [DirectoryTabsComponent] }).compileComponents();
    fixture = TestBed.createComponent(DirectoryTabsComponent);
    fixture.componentRef.setInput('tabs', [
      {
        id: 'one',
        label: 'Complete',
        location: { kind: 'filesystem', sourceId: 'downloads', path: '/Complete' },
      },
      {
        id: 'two',
        label: 'Movies',
        location: { kind: 'filesystem', sourceId: 'media', path: '/Movies' },
      },
    ]);
    fixture.componentRef.setInput('activeTabId', 'two');
    fixture.detectChanges();
  });

  it('uses accessible tab semantics and marks the active tab', () => {
    const tabList = fixture.nativeElement.querySelector('[role="tablist"]');
    const tabs = fixture.nativeElement.querySelectorAll('[role="tab"]');

    expect(tabList).toBeTruthy();
    expect(tabs).toHaveLength(2);
    expect(tabs[1].getAttribute('aria-selected')).toBe('true');
  });
});
