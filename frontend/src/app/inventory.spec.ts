import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Signal, Type } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Board } from './board';
import { Inventory } from './inventory';
import { PagedResult, Product } from './models';
import { StockRealtimeService } from './stock-realtime.service';
import { FakeHubConnection } from './testing/fake-hub-connection';

const product = (id: number, stock: number): Product => ({
  id,
  name: `Product ${id}`,
  sku: `SKU-${id}`,
  category: 'Bakery',
  unitPrice: 10,
  stock,
  reorderThreshold: 15,
});

const page = (items: Product[]): PagedResult<Product> => ({ items, total: items.length, page: 1, pageSize: 200 });

describe('stock pages', () => {
  let hub: FakeHubConnection;
  let http: HttpTestingController;
  let realtime: StockRealtimeService;

  beforeEach(() => {
    hub = new FakeHubConnection();
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), FakeHubConnection.provide(hub)],
    });
    http = TestBed.inject(HttpTestingController);
    realtime = TestBed.inject(StockRealtimeService);
  });

  const pages: [string, Type<{ products: Signal<Product[]> }>][] = [['Inventory', Inventory], ['Board', Board]];
  for (const [name, component] of pages) {
    describe(name, () => {
      it('applies a stock update received from another user', () => {
        const fixture = TestBed.createComponent(component);
        http.expectOne((r) => r.url === '/api/products').flush(page([product(1, 40), product(2, 30)]));

        hub.emit('StockUpdated', 1, 39);

        const stock = fixture.componentInstance.products().find((p) => p.id === 1)?.stock;
        expect(stock).toBe(39);
      });

      it('reloads its data when a refresh is requested after reconnecting', () => {
        TestBed.createComponent(component);
        http.expectOne((r) => r.url === '/api/products').flush(page([product(1, 40)]));

        realtime.refreshRequested.next();

        http.expectOne((r) => r.url === '/api/products');
      });

      it('unsubscribes from realtime streams when destroyed', () => {
        const fixture = TestBed.createComponent(component);
        http.expectOne((r) => r.url === '/api/products').flush(page([]));
        expect(realtime.stockUpdated.observed).toBeTrue();

        fixture.destroy();

        expect(realtime.stockUpdated.observed).toBeFalse();
        expect(realtime.refreshRequested.observed).toBeFalse();
      });
    });
  }
});
