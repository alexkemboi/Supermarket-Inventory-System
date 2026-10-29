import { HubConnection } from '@microsoft/signalr';
import { STOCK_HUB_CONNECTION } from '../stock-realtime.service';

type Handler = (...args: any[]) => unknown;

/** In-memory stand-in for a SignalR HubConnection that lets specs raise server events. */
export class FakeHubConnection {
  readonly handlers = new Map<string, Handler[]>();
  readonly invoked: string[] = [];
  connectionId: string | null = 'connection-1';
  startCalls = 0;
  stopCalls = 0;
  failStart = false;
  private closeHandler?: Handler;
  private reconnectingHandler?: Handler;
  private reconnectedHandler?: Handler;

  on(event: string, handler: Handler): void {
    this.handlers.set(event, [...(this.handlers.get(event) ?? []), handler]);
  }

  onclose(handler: Handler): void { this.closeHandler = handler; }
  onreconnecting(handler: Handler): void { this.reconnectingHandler = handler; }
  onreconnected(handler: Handler): void { this.reconnectedHandler = handler; }

  async start(): Promise<void> {
    this.startCalls++;
    if (this.failStart) throw new Error('hub unavailable');
  }

  async stop(): Promise<void> {
    this.stopCalls++;
    this.closeHandler?.();
  }

  async invoke(method: string): Promise<void> {
    this.invoked.push(method);
  }

  /** Simulates the server sending an event to this client. */
  emit(event: string, ...args: unknown[]): void {
    for (const handler of this.handlers.get(event) ?? []) handler(...args);
  }

  close(): void { this.closeHandler?.(); }
  reconnecting(): void { this.reconnectingHandler?.(); }
  async reconnected(): Promise<void> { await this.reconnectedHandler?.('connection-2'); }

  static provide(fake: FakeHubConnection) {
    return { provide: STOCK_HUB_CONNECTION, useValue: fake as unknown as HubConnection };
  }
}
