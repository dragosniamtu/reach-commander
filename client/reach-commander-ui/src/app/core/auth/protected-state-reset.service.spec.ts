import { ProtectedStateResetService } from './protected-state-reset.service';

describe('ProtectedStateResetService', () => {
  it('synchronously invokes the registered workspace teardown', () => {
    const handler = vi.fn();
    const service = new ProtectedStateResetService();
    service.register(handler);

    service.reset();

    expect(handler).toHaveBeenCalledOnce();
  });

  it('does not call a workspace teardown after it unregisters', () => {
    const handler = vi.fn();
    const service = new ProtectedStateResetService();
    const unregister = service.register(handler);

    unregister();
    service.reset();

    expect(handler).not.toHaveBeenCalled();
  });
});
