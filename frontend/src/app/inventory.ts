import { CurrencyPipe } from '@angular/common';
import { Component, computed, DestroyRef, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { finalize, Observable } from 'rxjs';
import { ApiService } from './api.service';
import { Product } from './models';
import { StockRealtimeService } from './stock-realtime.service';

@Component({
  selector: 'app-inventory',
  imports: [CurrencyPipe, FormsModule],
  templateUrl: './inventory.html',
  styleUrl: './inventory.css',
})
export class Inventory {
  readonly products = signal<Product[]>([]);
  readonly total = signal(0);
  readonly loading = signal(false);
  readonly error = signal('');
  readonly busyProductId = signal<number | null>(null);
  readonly page = signal(1);
  readonly pageSize = signal(25);
  readonly pageSizes = [25, 50, 100];
  readonly totalPages = computed(() => Math.max(1, Math.ceil(this.total() / this.pageSize())));
  readonly rangeStart = computed(() => (this.total() === 0 ? 0 : (this.page() - 1) * this.pageSize() + 1));
  readonly rangeEnd = computed(() => Math.min(this.page() * this.pageSize(), this.total()));
  search = '';
  quantity = 1;

  private readonly api = inject(ApiService);
  private readonly realtime = inject(StockRealtimeService);
  private readonly destroyRef = inject(DestroyRef);

  constructor() {
    this.realtime.stockUpdated
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(({ productId, stock }) => this.patchStock(productId, stock));
    this.realtime.refreshRequested
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.load());

    this.load();
    void this.realtime.start().catch(() => this.error.set('Live updates are unavailable.'));
  }

  /** Runs a new search from the first page. */
  runQuery(): void {
    this.page.set(1);
    this.load();
  }

  goToPage(page: number): void {
    const target = Math.min(Math.max(1, page), this.totalPages());
    if (target === this.page()) return;
    this.page.set(target);
    this.load();
  }

  changePageSize(size: number): void {
    // Keep the first visible row on screen when the page size changes.
    const firstRow = (this.page() - 1) * this.pageSize();
    this.pageSize.set(Number(size));
    this.page.set(Math.floor(firstRow / this.pageSize()) + 1);
    this.load();
  }

  /** Reloads the current page (after a search, an order, a restock or a reconnect). */
  load(): void {
    this.loading.set(true);
    this.error.set('');
    this.api
      .products(this.search, this.pageSize(), this.page())
      .pipe(finalize(() => this.loading.set(false)))
      .subscribe({
        next: (result) => {
          this.total.set(result.total);
          // The result set shrank below the current page (e.g. a narrower search): show the last page.
          if (result.items.length === 0 && result.total > 0 && this.page() > 1) {
            this.page.set(this.totalPages());
            this.load();
            return;
          }
          this.products.set(result.items);
        },
        error: () => this.error.set('Could not load inventory.'),
      });
  }

  order(product: Product): void {
    const quantity = Math.max(1, Math.floor(this.quantity));
    this.runAction(
      product.id,
      this.api.placeOrder(product.id, quantity, this.realtime.connectionId),
      'Order could not be placed.',
    );
  }

  restock(product: Product): void {
    const quantity = Math.max(1, Math.floor(this.quantity));
    this.runAction(product.id, this.api.restock(product.id, quantity), 'Product could not be restocked.');
  }

  stockState(product: Product): 'critical' | 'watch' | 'healthy' {
    if (product.stock < product.reorderThreshold) return 'critical';
    if (product.stock < product.reorderThreshold * 2) return 'watch';
    return 'healthy';
  }

  private runAction(productId: number, action: Observable<unknown>, message: string): void {
    this.busyProductId.set(productId);
    this.error.set('');
    action.pipe(finalize(() => this.busyProductId.set(null))).subscribe({
      next: () => this.load(),
      error: () => this.error.set(message),
    });
  }

  private patchStock(productId: number, stock: number): void {
    this.products.update((products) =>
      products.map((product) => (product.id === productId ? { ...product, stock } : product)),
    );
  }
}
