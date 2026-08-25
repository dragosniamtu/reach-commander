import { TestBed } from '@angular/core/testing';
import { SwUpdate, VersionEvent } from '@angular/service-worker';
import { EMPTY, Subject } from 'rxjs';
import {
  BeforeInstallPromptEvent,
  PWA_RELOAD,
  PwaService,
} from './pwa.service';

describe('PwaService', () => {
  let service: PwaService;
  let versionEvents: Subject<VersionEvent>;
  let reload: ReturnType<typeof vi.fn>;
  let checkForUpdate: ReturnType<typeof vi.fn>;
  let activateUpdate: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    versionEvents = new Subject<VersionEvent>();
    reload = vi.fn();
    checkForUpdate = vi.fn(() => Promise.resolve(true));
    activateUpdate = vi.fn(() => Promise.resolve(true));
    TestBed.configureTestingModule({
      providers: [
        PwaService,
        {
          provide: SwUpdate,
          useValue: {
            isEnabled: true,
            versionUpdates: versionEvents.asObservable(),
            unrecoverable: EMPTY,
            checkForUpdate,
            activateUpdate,
          },
        },
        { provide: PWA_RELOAD, useValue: reload },
      ],
    });
    service = TestBed.inject(PwaService);
  });

  it('captures one deferred install prompt and clears it after dismissal', async () => {
    const prompt = vi.fn(() => Promise.resolve());

    window.dispatchEvent(installPromptEvent(prompt, 'dismissed'));

    expect(service.canInstall()).toBe(true);
    await service.install();
    expect(prompt).toHaveBeenCalledOnce();
    expect(service.canInstall()).toBe(false);
  });

  it('clears the install action after the app is installed', () => {
    window.dispatchEvent(installPromptEvent(vi.fn(), 'accepted'));

    window.dispatchEvent(new Event('appinstalled'));

    expect(service.canInstall()).toBe(false);
  });

  it('tracks browser offline and online transitions', () => {
    window.dispatchEvent(new Event('offline'));
    expect(service.online()).toBe(false);

    window.dispatchEvent(new Event('online'));
    expect(service.online()).toBe(true);
  });

  it('offers a ready version and reloads only after explicit acceptance', () => {
    versionEvents.next({
      type: 'VERSION_READY',
      currentVersion: { hash: 'current' },
      latestVersion: { hash: 'latest' },
    });

    expect(service.updateReady()).toBe(true);
    expect(reload).not.toHaveBeenCalled();

    service.reloadForUpdate();

    expect(reload).toHaveBeenCalledOnce();
  });

  it('keeps the current app usable when installation fails', async () => {
    window.dispatchEvent(failingInstallPromptEvent());

    await service.install();

    expect(service.installing()).toBe(false);
    expect(service.error()).toContain('installation');
  });

  it('keeps the current app usable when an update download fails', () => {
    versionEvents.next({
      type: 'VERSION_INSTALLATION_FAILED',
      version: { hash: 'failed' },
      error: 'network failed',
    });

    expect(service.updateReady()).toBe(false);
    expect(service.error()).toContain('update');
  });

  it('activates the matching shell and reloads exactly once after a system update', async () => {
    await Promise.all([
      service.refreshAfterSystemUpdate(),
      service.refreshAfterSystemUpdate(),
    ]);

    expect(checkForUpdate).toHaveBeenCalledOnce();
    expect(activateUpdate).toHaveBeenCalledOnce();
    expect(reload).toHaveBeenCalledOnce();
  });
});

function installPromptEvent(
  prompt: () => Promise<void>,
  outcome: 'accepted' | 'dismissed',
): BeforeInstallPromptEvent {
  const event = new Event('beforeinstallprompt', { cancelable: true });
  Object.defineProperties(event, {
    prompt: { value: prompt },
    userChoice: { value: Promise.resolve({ outcome, platform: 'web' }) },
  });
  return event as BeforeInstallPromptEvent;
}

function failingInstallPromptEvent(): BeforeInstallPromptEvent {
  return installPromptEvent(
    () => Promise.reject(new Error('prompt unavailable')),
    'dismissed',
  );
}
