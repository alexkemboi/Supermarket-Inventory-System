import { fakeAsync, flushMicrotasks, TestBed, tick } from '@angular/core/testing';
import { LowStockAlert } from './models';
import { StockRealtimeService } from './stock-realtime.service';
import { FakeHubConnection } from './testing/fake-hub-connection';

describe('StockRealtimeService', () => {
  let hub: FakeHubConnection;
  let service: StockRealtimeService;

  const alert = (productId: number, stock: number): LowStockAlert => ({
    productId,
    productName: `Product ${productId}`,
    stock,
    reorderThreshold: 15,
  });

  beforeEach(() => {
    hub = new FakeHubConnection();
    TestBed.configureTestingModule({ providers: [FakeHubConnection.provide(hub)] });
    service = TestBed.inject(StockRealtimeService);
  });

  it('registers exactly one listener per server event', () => {
    expect(hub.handlers.get('StockUpdated')?.length).toBe(1);
    expect(hub.handlers.get('LowStockAlert')?.length).toBe(1);
  });

  it('connects once and joins the stock watchers group even when started repeatedly', async () => {
    await Promise.all([service.start(), service.start()]);
    await service.start();

    expect(hub.startCalls).toBe(1);
    expect(hub.invoked).toEqual(['JoinStockWatchers']);
    expect(service.state()).toBe('live');
  });

  it('publishes incoming stock updates', () => {
    const received: unknown[] = [];
    service.stockUpdated.subscribe((update) => received.push(update));

    hub.emit('StockUpdated', 7, 41);

    expect(received).toEqual([{ productId: 7, stock: 41 }]);
  });

  it('keeps repeated alerts for the same product as distinct notices, newest first', () => {
    hub.emit('LowStockAlert', alert(3, 14));
    hub.emit('LowStockAlert', alert(3, 12));

    const alerts = service.alerts();
    expect(alerts.map((a) => a.stock)).toEqual([12, 14]);
    expect(new Set(alerts.map((a) => a.id)).size).toBe(2);
  });

  it('caps the alert list at five and allows dismissing one alert', () => {
    for (let i = 1; i <= 7; i++) hub.emit('LowStockAlert', alert(i, 10));

    expect(service.alerts().map((a) => a.productId)).toEqual([7, 6, 5, 4, 3]);

    service.dismissAlert(service.alerts()[1].id);
    expect(service.alerts().map((a) => a.productId)).toEqual([7, 5, 4, 3]);
  });

  it('rejoins the group and requests a data refresh after an automatic reconnect', async () => {
    await service.start();
    let refreshes = 0;
    service.refreshRequested.subscribe(() => refreshes++);

    hub.reconnecting();
    expect(service.state()).toBe('connecting');
    await hub.reconnected();

    expect(hub.invoked).toEqual(['JoinStockWatchers', 'JoinStockWatchers']);
    expect(service.state()).toBe('live');
    expect(refreshes).toBe(1);
  });

  it('restarts the connection after it closes and refreshes data once live again', fakeAsync(() => {
    void service.start();
    flushMicrotasks();
    let refreshes = 0;
    service.refreshRequested.subscribe(() => refreshes++);

    hub.close();
    expect(service.state()).toBe('offline');

    tick(1000);
    flushMicrotasks();

    expect(hub.startCalls).toBe(2);
    expect(service.state()).toBe('live');
    expect(refreshes).toBe(1);
  }));

  it('reports a failed start and keeps retrying with backoff', fakeAsync(() => {
    hub.failStart = true;
    let failed = false;
    service.start().catch(() => (failed = true));
    flushMicrotasks();

    expect(failed).toBeTrue();
    expect(service.state()).toBe('offline');

    hub.failStart = false;
    tick(1000);
    flushMicrotasks();

    expect(hub.startCalls).toBe(2);
    expect(service.state()).toBe('live');
  }));
});
