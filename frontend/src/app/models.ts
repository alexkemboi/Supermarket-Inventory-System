export interface Product {
  id: number;
  name: string;
  sku: string;
  category: string;
  unitPrice: number;
  stock: number;
  reorderThreshold: number;
}

export interface PagedResult<T> {
  items: T[];
  total: number;
  page: number;
  pageSize: number;
}

/** Payload of the SignalR "LowStockAlert" event (backend LowStockAlertDto). */
export interface LowStockAlert {
  productId: number;
  productName: string;
  stock: number;
  reorderThreshold: number;
}

/** A received alert as held by the client; the id keeps repeated alerts for one product distinct. */
export interface LowStockNotice extends LowStockAlert {
  id: number;
  receivedAt: Date;
}
