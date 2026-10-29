import { inject, Injectable, InjectionToken, signal } from '@angular/core';
import * as signalR from '@microsoft/signalr';
import { Subject } from 'rxjs';
import { AuthService } from './auth.service';
import { LowStockAlert, LowStockNotice } from './models';

export const STOCK_HUB_CONNECTION = new InjectionToken<signalR.HubConnection>('STOCK_HUB_CONNECTION', {
  providedIn: 'root',
  factory: () => {
    const auth = inject(AuthService);
    return new signalR.HubConnectionBuilder()
      // Read on every (re)connect, so a fresh sign-in is picked up without rebuilding the connection.
      .withUrl('/hubs/stock', { accessTokenFactory: () => auth.token() ?? '' })
      .withAutomaticReconnect()
      .build();
  },
});

const MAX_ALERTS = 5;

@Injectable({ providedIn: 'root' })
export class StockRealtimeService {
  readonly state = signal<'offline' | 'connecting' | 'live'>('offline');
  readonly alerts = signal<LowStockNotice[]>([]);
  readonly stockUpdated = new Subject<{ productId: number; stock: number }>();
  readonly refreshRequested = new Subject<void>();

  get connectionId(): string | undefined {
    return this.connection.connectionId ?? undefined;
  }

  private started = false;
  /** True between start() and stop(); a close while wanted schedules a restart, otherwise it is final. */
  private wanted = false;
  private nextAlertId = 1;
  private restartAttempts = 0;
  private restartTimer: ReturnType<typeof setTimeout> | undefined;
  private readonly connection = inject(STOCK_HUB_CONNECTION);

  // Hub handlers are registered once, here, for the lifetime of this root singleton. Components only
  // subscribe to the subjects/signals above, so re-creating a component never adds a second listener.
  constructor() {
    this.connection.on('StockUpdated', (productId: number, stock: number) => {
      this.stockUpdated.next({ productId, stock });
    });
    this.connection.on('LowStockAlert', (alert: LowStockAlert) => {
      const notice: LowStockNotice = { ...alert, id: this.nextAlertId++, receivedAt: new Date() };
      this.alerts.update((alerts) => [notice, ...alerts].slice(0, MAX_ALERTS));
    });
    this.connection.onreconnecting(() => this.state.set('connecting'));
    this.connection.onclose(() => {
      this.started = false;
      this.state.set('offline');
      if (this.wanted) this.scheduleRestart();
    });
    this.connection.onreconnected(async () => {
      await this.connection.invoke('JoinStockWatchers');
      this.state.set('live');
      this.refreshRequested.next();
    });
  }

  async start(): Promise<void> {
    this.wanted = true;
    if (this.started) return;
    this.started = true;
    this.state.set('connecting');

    try {
      await this.connection.start();
      await this.connection.invoke('JoinStockWatchers');
      this.state.set('live');
      if (this.restartAttempts > 0) this.refreshRequested.next();
      this.restartAttempts = 0;
    } catch (error) {
      this.started = false;
      this.state.set('offline');
      if (this.wanted) this.scheduleRestart();
      throw error;
    }
  }

  /** Disconnects for good (until the next start), e.g. when the user signs out. */
  async stop(): Promise<void> {
    if (!this.wanted && !this.started) return;
    this.wanted = false;
    clearTimeout(this.restartTimer);
    this.restartTimer = undefined;
    this.restartAttempts = 0;
    this.alerts.set([]);
    try {
      await this.connection.stop();
    } finally {
      this.started = false;
      this.state.set('offline');
    }
  }

  dismissAlert(id: number): void {
    this.alerts.update((alerts) => alerts.filter((alert) => alert.id !== id));
  }

  // withAutomaticReconnect gives up after ~40s; keep retrying (capped backoff) so a longer API outage
  // does not leave the page permanently offline. Data is reloaded once the connection is back.
  private scheduleRestart(): void {
    if (this.restartTimer !== undefined) return;
    const delay = Math.min(30_000, 1_000 * 2 ** this.restartAttempts++);
    this.restartTimer = setTimeout(() => {
      this.restartTimer = undefined;
      if (this.wanted) void this.start().catch(() => undefined);
    }, delay);
  }
}
