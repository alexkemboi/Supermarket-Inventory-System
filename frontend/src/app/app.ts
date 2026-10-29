import { Component, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { LowStockAlerts } from './low-stock-alerts';
import { StockRealtimeService } from './stock-realtime.service';

@Component({
  selector: 'app-root',
  imports: [LowStockAlerts, RouterLink, RouterLinkActive, RouterOutlet],
  templateUrl: './app.html',
  styleUrl: './app.css',
})
export class App {
  readonly realtime = inject(StockRealtimeService);
}
