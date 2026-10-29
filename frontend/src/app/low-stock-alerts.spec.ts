import { TestBed } from '@angular/core/testing';
import { LowStockAlerts } from './low-stock-alerts';
import { FakeHubConnection } from './testing/fake-hub-connection';

describe('LowStockAlerts', () => {
  let hub: FakeHubConnection;

  beforeEach(() => {
    hub = new FakeHubConnection();
    TestBed.configureTestingModule({ providers: [FakeHubConnection.provide(hub)] });
  });

  const toasts = (element: HTMLElement) =>
    [...element.querySelectorAll('.toast')].map((toast) =>
      [...toast.querySelectorAll('span, strong, p')].map((part) => part.textContent!.trim()).join(' | '));

  it('renders the product, current stock and reorder threshold of each alert', () => {
    const fixture = TestBed.createComponent(LowStockAlerts);
    hub.emit('LowStockAlert', { productId: 4, productName: 'Hass Avocados 4 Pack', stock: 14, reorderThreshold: 15 });
    fixture.detectChanges();

    const element: HTMLElement = fixture.nativeElement;
    expect(toasts(element)).toEqual(['Low stock | Hass Avocados 4 Pack | 14 left · reorder at 15']);
    expect(element.querySelector('.toasts')?.getAttribute('aria-live')).toBe('polite');
  });

  it('removes an alert when it is dismissed', () => {
    const fixture = TestBed.createComponent(LowStockAlerts);
    hub.emit('LowStockAlert', { productId: 1, productName: 'Milk', stock: 3, reorderThreshold: 15 });
    hub.emit('LowStockAlert', { productId: 2, productName: 'Bread', stock: 9, reorderThreshold: 15 });
    fixture.detectChanges();

    const dismissMilk = fixture.nativeElement.querySelector('button[aria-label="Dismiss low stock alert for Milk"]');
    dismissMilk.click();
    fixture.detectChanges();

    expect(toasts(fixture.nativeElement)).toEqual(['Low stock | Bread | 9 left · reorder at 15']);
  });

  it('shows each alert once after the component is destroyed and created again', () => {
    TestBed.createComponent(LowStockAlerts).destroy();
    const fixture = TestBed.createComponent(LowStockAlerts);

    hub.emit('LowStockAlert', { productId: 5, productName: 'Eggs', stock: 2, reorderThreshold: 15 });
    fixture.detectChanges();

    expect(toasts(fixture.nativeElement).length).toBe(1);
    expect(hub.handlers.get('LowStockAlert')?.length).toBe(1);
  });
});
