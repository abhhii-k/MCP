// Demo Angular component for the Step 10 investigation (illustrative; not compiled).
import { Component, Input, OnInit, inject, signal } from '@angular/core';
import { CurrencyPipe } from '@angular/common';
import { HttpClient } from '@angular/common/http';

interface OrderItem { productId: number; name: string; quantity: number; unitPrice: number; }
interface Order { orderId: number; status: string; totalAmount: number | null; items: OrderItem[]; }

@Component({
  selector: 'app-order-summary',
  standalone: true,
  imports: [CurrencyPipe],
  template: `
    @if (order(); as o) {
      <h2>Order #{{ o.orderId }} ({{ o.status }})</h2>
      <table>
        @for (item of o.items; track item.productId) {
          <tr>
            <td>{{ item.name }}</td>
            <td>{{ item.quantity }} × {{ item.unitPrice | currency:'EUR' }}</td>
            <td>{{ item.quantity * item.unitPrice | currency:'EUR' }}</td>
          </tr>
        }
      </table>
      <!-- Total comes straight from the API (Orders.TotalAmount) -->
      <p class="total">Total: {{ o.totalAmount | currency:'EUR' }}</p>
    }
  `,
})
export class OrderSummaryComponent implements OnInit {
  @Input({ required: true }) orderId!: number;

  private readonly http = inject(HttpClient);
  readonly order = signal<Order | null>(null);

  ngOnInit(): void {
    this.http.get<Order>(`/api/orders/${this.orderId}`).subscribe(o => this.order.set(o));
  }
}
