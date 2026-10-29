import { Component, inject } from '@angular/core';
import { StockRealtimeService } from './stock-realtime.service';

/** Global low-stock toasts, rendered from the shared StockRealtimeService alert list. */
@Component({
  selector: 'app-low-stock-alerts',
  template: `
    <section class="toasts" aria-live="polite" aria-label="Low stock alerts">
      @for (alert of realtime.alerts(); track alert.id) {
        <article class="toast" role="status">
          <div>
            <span>Low stock</span>
            <strong>{{ alert.productName }}</strong>
            <p>{{ alert.stock }} left · reorder at {{ alert.reorderThreshold }}</p>
          </div>
          <button type="button" (click)="realtime.dismissAlert(alert.id)"
                  [attr.aria-label]="'Dismiss low stock alert for ' + alert.productName">×</button>
        </article>
      }
    </section>
  `,
  styles: `
    .toasts { display: grid; gap: .6rem; position: fixed; right: 1rem; top: 5.25rem; width: min(22rem, calc(100vw - 2rem)); z-index: 10; }
    .toast { align-items: start; background: var(--accent); box-shadow: 0 10px 30px rgb(0 0 0 / .45); color: var(--ink); display: flex; gap: 1rem; justify-content: space-between; padding: .85rem 1rem; }
    span { display: block; font: 700 .6rem var(--mono); letter-spacing: .14em; text-transform: uppercase; }
    strong { display: block; font-weight: 700; margin: .25rem 0; }
    p { font: 600 .72rem var(--mono); margin: 0; }
    button { background: transparent; border: 1px solid var(--ink); color: var(--ink); cursor: pointer; font: 700 1rem/1 var(--mono); height: 1.8rem; width: 1.8rem; }
    button:focus-visible { outline: 2px solid var(--ink); }
  `,
})
export class LowStockAlerts {
  readonly realtime = inject(StockRealtimeService);
}
