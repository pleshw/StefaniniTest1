/** One line of an order or a quote, priced by Orders. */
export interface OrderItem {
  readonly sku: string;
  readonly productName: string;
  readonly unitPrice: number;
  readonly catalogVersion: number;
  readonly quantity: number;
  readonly lineDiscount: number;
  readonly lineTotal: number;
}

/** The cart priced by Orders with the active discounts; nothing is stored. */
export interface Quote {
  readonly currency: string;
  readonly itemCount: number;
  readonly subtotal: number;
  readonly discountTotal: number;
  readonly total: number;
  readonly items: readonly OrderItem[];
}

export interface Client {
  readonly id: string;
  readonly name: string;
}

/** An order with its items, as Orders returns it. */
export interface Order {
  readonly id: string;
  readonly number: string;
  readonly client: Client;
  readonly status: string;
  readonly channel: string;
  readonly currency: string;
  readonly itemCount: number;
  readonly subtotal: number;
  readonly discountTotal: number;
  readonly total: number;
  readonly items: readonly OrderItem[];
  readonly createdAt: string;
  /** The version admins send as If-Match to change the status. */
  readonly rowVersion: number;
}

/** What an order list shows of each order. */
export type OrderSummary = Omit<Order, 'items' | 'rowVersion'>;

export interface OrderLineRequest {
  readonly sku: string;
  readonly quantity: number;
}

/** The body of a quote and of a new order. */
export interface OrderRequest {
  readonly currency: string;
  readonly items: readonly OrderLineRequest[];
}
