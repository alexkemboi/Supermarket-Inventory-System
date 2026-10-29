import { Component, effect, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { AuthService } from './auth.service';
import { LowStockAlerts } from './low-stock-alerts';
import { StockRealtimeService } from './stock-realtime.service';

@Component({
  selector: 'app-root',
  imports: [LowStockAlerts, RouterLink, RouterLinkActive, RouterOutlet],
  templateUrl: './app.html',
  styleUrl: './app.css',
})
export class App {
  readonly auth = inject(AuthService);
  readonly realtime = inject(StockRealtimeService);

  constructor() {
    // However the session ends (sign-out, expiry, a 401 from the API), drop the live connection.
    effect(() => {
      if (!this.auth.user()) void this.realtime.stop();
    });
  }

  signOut(): void {
    this.auth.logout();
  }
}
