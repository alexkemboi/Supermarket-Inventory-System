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

  describe('Inventory pagination', () => {
    const products = (from: number, count: number) => Array.from({ length: count }, (_, i) => product(from + i, 50));
    const flushPage = (items: Product[], total: number, pageNumber: number, pageSize: number) =>
      http.expectOne((r) => r.url === '/api/products').flush({ items, total, page: pageNumber, pageSize });

    it('requests the first page of 25 and shows the visible range', () => {
      const fixture = TestBed.createComponent(Inventory);
      const request = http.expectOne((r) => r.url === '/api/products');
      expect(request.request.params.get('page')).toBe('1');
      expect(request.request.params.get('pageSize')).toBe('25');
      request.flush({ items: products(1, 25), total: 60, page: 1, pageSize: 25 });
      fixture.detectChanges();

      const inventory = fixture.componentInstance;
      expect(inventory.totalPages()).toBe(3);
      expect(fixture.nativeElement.querySelector('.range').textContent).toContain('1–25');
      expect(fixture.nativeElement.querySelector('.range').textContent).toContain('60');
    });

    it('moves between pages and stays within bounds', () => {
      const inventory = TestBed.createComponent(Inventory).componentInstance;
      flushPage(products(1, 25), 60, 1, 25);

      inventory.goToPage(3);
      expect(http.expectOne((r) => r.url === '/api/products').request.params.get('page')).toBe('3');
      expect(inventory.rangeStart()).toBe(51);
      expect(inventory.rangeEnd()).toBe(60);

      inventory.goToPage(99);
      http.expectNone((r) => r.url === '/api/products');
      expect(inventory.page()).toBe(3);
    });

    it('starts a new search from the first page', () => {
      const inventory = TestBed.createComponent(Inventory).componentInstance;
      flushPage(products(1, 25), 60, 1, 25);
      inventory.goToPage(2);
      flushPage(products(26, 25), 60, 2, 25);

      inventory.search = 'milk';
      inventory.runQuery();

      const request = http.expectOne((r) => r.url === '/api/products');
      expect(request.request.params.get('page')).toBe('1');
      expect(request.request.params.get('search')).toBe('milk');
    });

    it('keeps the first visible row when the page size changes', () => {
      const inventory = TestBed.createComponent(Inventory).componentInstance;
      flushPage(products(1, 25), 300, 1, 25);
      inventory.goToPage(5);
      flushPage(products(101, 25), 300, 5, 25);

      inventory.changePageSize(100);

      const request = http.expectOne((r) => r.url === '/api/products');
      expect(request.request.params.get('pageSize')).toBe('100');
      expect(request.request.params.get('page')).toBe('2');
    });

    it('falls back to the last page when the result set shrinks', () => {
      const inventory = TestBed.createComponent(Inventory).componentInstance;
      flushPage(products(1, 25), 60, 1, 25);
      inventory.goToPage(3);
      flushPage([], 30, 3, 25);

      const retry = http.expectOne((r) => r.url === '/api/products');
      expect(retry.request.params.get('page')).toBe('2');
      retry.flush({ items: products(26, 5), total: 30, page: 2, pageSize: 25 });
      expect(inventory.products().length).toBe(5);
    });
  });
});
