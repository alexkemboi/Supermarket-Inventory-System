import { Injectable, signal } from '@angular/core';
import * as signalR from '@microsoft/signalr';
import { Subject } from 'rxjs';
import { LowStockAlert } from './models';

@Injectable({ providedIn: 'root' })
export class StockRealtimeService {
  readonly state = signal<'offline' | 'connecting' | 'live'>('offline');
  readonly alerts = signal<LowStockAlert[]>([]);
  readonly stockUpdated = new Subject<{ productId: number; stock: number }>();
  readonly refreshRequested = new Subject<void>();

  get connectionId(): string | undefined {
    return this.connection.connectionId ?? undefined;
  }

  private started = false;
  private readonly connection = new signalR.HubConnectionBuilder()
    .withUrl('/hubs/stock')
    .withAutomaticReconnect()
    .build();

  constructor() {
    this.connection.on('StockUpdated', (productId: number, stock: number) => {
      this.stockUpdated.next({ productId, stock });
    });
    this.connection.on('LowStockAlert', (alert: LowStockAlert) => {
      this.alerts.update((alerts) => [alert, ...alerts].slice(0, 5));
    });
    this.connection.onreconnecting(() => this.state.set('connecting'));
    this.connection.onclose(() => {
      this.started = false;
      this.state.set('offline');
      this.scheduleRestart();
    });
    this.connection.onreconnected(async () => {
      await this.connection.invoke('JoinStockWatchers');
      this.state.set('live');
      this.refreshRequested.next();
    });
  }

  async start(): Promise<void> {
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
      this.scheduleRestart();
      throw error;
    }
  }

  // withAutomaticReconnect gives up after ~40s; keep retrying (capped backoff) so a longer API outage
  // does not leave the page permanently offline. Data is reloaded once the connection is back.
  private restartAttempts = 0;
  private restartTimer: ReturnType<typeof setTimeout> | undefined;

  private scheduleRestart(): void {
    if (this.restartTimer !== undefined) return;
    const delay = Math.min(30_000, 1_000 * 2 ** this.restartAttempts++);
    this.restartTimer = setTimeout(() => {
      this.restartTimer = undefined;
      void this.start().catch(() => undefined);
    }, delay);
  }
}
