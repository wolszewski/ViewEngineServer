export type RowData = Record<string, string | null>;

export interface SnapshotEvent {
    type: 'snapshot';
    subscriptionId: number;
    totalCount: number;
    startIndex: number;
    rows: RowData[];
}

export interface RowUpdateEvent {
    type: 'rowUpdate';
    subscriptionId: number;
    rowId: string;
    position: number;
    changedFields: RowData;
}

export interface RowInsertEvent {
    type: 'rowInsert';
    subscriptionId: number;
    position: number;
    row: RowData;
}

export interface RowRemoveEvent {
    type: 'rowRemove';
    subscriptionId: number;
    position: number;
}

export type DeltaEvent = SnapshotEvent | RowUpdateEvent | RowInsertEvent | RowRemoveEvent;

interface SubscriptionAcceptedMessage {
    type: 'subscriptionAccepted';
    subscriptionId: number;
}

export interface FilterRequest {
    field: string;
    operator: string;
    value: string;
}

export interface SubscribeRequest {
    collectionId: string;
    sortColumn: string;
    sortAscending: boolean;
    pageSize: number;
    startIndex: number;
    filters: FilterRequest[];
    fields?: string[];
}

interface ClientCallbacks {
    onStatus: (status: string) => void;
    onEvent: (event: DeltaEvent) => void;
}

export class WebHostClient {
    private readonly webSocketUrl: string;
    private readonly callbacks: ClientCallbacks;

    private socket: WebSocket | null = null;
    private subscribeRetryHandle: number | null = null;
    private hasReceivedSnapshot = false;
    private lastSubscribe: SubscribeRequest | null = null;
    private activeSubscriptionId: number | null = null;

    public constructor(webSocketUrl: string, callbacks: ClientCallbacks) {
        this.webSocketUrl = webSocketUrl;
        this.callbacks = callbacks;
    }

    public get isConnected(): boolean {
        return this.socket?.readyState === WebSocket.OPEN;
    }

    public connect(request: SubscribeRequest): void {
        if (this.socket && this.socket.readyState === WebSocket.OPEN) {
            this.sendSubscribe(request);
            return;
        }

        this.disconnect();
        this.lastSubscribe = request;
        this.hasReceivedSnapshot = false;
        this.activeSubscriptionId = null;
        this.callbacks.onStatus('Connecting...');

        const socket = new WebSocket(this.webSocketUrl);
        this.socket = socket;

        socket.addEventListener('open', () => {
            this.callbacks.onStatus('Connected');
            const subscribe = this.lastSubscribe ?? request;
            this.sendSubscribe(subscribe);
            this.startSubscribeRetry();
        });

        socket.addEventListener('message', (event) => {
            const parsed = JSON.parse(String(event.data)) as SubscriptionAcceptedMessage | DeltaEvent[];
            if (!Array.isArray(parsed)) {
                if (parsed.type === 'subscriptionAccepted') {
                    this.activeSubscriptionId = parsed.subscriptionId;
                }
                return;
            }

            for (const delta of parsed) {
                if (this.activeSubscriptionId !== null && delta.subscriptionId !== this.activeSubscriptionId) {
                    continue;
                }

                if (delta.type === 'snapshot') {
                    this.hasReceivedSnapshot = true;
                    this.stopSubscribeRetry();
                    this.callbacks.onStatus('Connected');
                    this.ensureSnapshotMatchesRequestedViewport(delta);
                }
                this.callbacks.onEvent(delta);
            }
        });

        socket.addEventListener('close', () => {
            this.stopSubscribeRetry();
            this.hasReceivedSnapshot = false;
            if (this.socket === socket) {
                this.socket = null;
            }
            this.callbacks.onStatus('Disconnected');
        });
    }

    public setViewport(startIndex: number, pageSize: number): void {
        if (this.lastSubscribe) {
            this.lastSubscribe = {
                ...this.lastSubscribe,
                startIndex,
                pageSize
            };
        }

        if (!this.socket || this.socket.readyState !== WebSocket.OPEN) {
            return;
        }

        if (!this.lastSubscribe) {
            return;
        }

        if (this.activeSubscriptionId === null) {
            this.sendSubscribe(this.lastSubscribe);
            return;
        }

        this.sendSetViewport(startIndex, pageSize);
    }

    public disconnect(): void {
        this.stopSubscribeRetry();
        const socket = this.socket;
        this.socket = null;
        this.hasReceivedSnapshot = false;
        this.activeSubscriptionId = null;
        if (socket) {
            socket.close();
        }
    }

    private sendSubscribe(request: SubscribeRequest): void {
        if (!this.socket || this.socket.readyState !== WebSocket.OPEN) {
            return;
        }

        this.lastSubscribe = request;
        const message: Record<string, unknown> = {
            type: 'subscribe',
            collectionId: request.collectionId,
            sortColumn: request.sortColumn,
            sortAscending: request.sortAscending,
            startIndex: request.startIndex,
            pageSize: request.pageSize,
            filters: (request.filters ?? []).map((filter) => ({
                field: filter.field,
                operator: filter.operator,
                value: filter.value
            }))
        };

        if (this.activeSubscriptionId !== null) {
            message.subscriptionId = this.activeSubscriptionId;
        }

        if (request.fields && request.fields.length > 0) {
            message.fields = request.fields;
        }

        this.socket.send(JSON.stringify(message));
    }

    private ensureSnapshotMatchesRequestedViewport(snapshot: SnapshotEvent): void {
        if (!this.lastSubscribe) {
            return;
        }

        if (snapshot.startIndex === this.lastSubscribe.startIndex) {
            return;
        }

        this.sendSubscribe(this.lastSubscribe);
    }

    private sendSetViewport(startIndex: number, pageSize: number): void {
        if (!this.socket || this.socket.readyState !== WebSocket.OPEN || this.activeSubscriptionId === null) {
            return;
        }

        this.socket.send(JSON.stringify({
            type: 'setviewport',
            subscriptionId: this.activeSubscriptionId,
            startIndex,
            pageSize
        }));
    }

    private startSubscribeRetry(): void {
        this.stopSubscribeRetry();
        this.subscribeRetryHandle = window.setInterval(() => {
            if (!this.socket || this.socket.readyState !== WebSocket.OPEN) {
                this.stopSubscribeRetry();
                return;
            }

            if (this.hasReceivedSnapshot) {
                this.stopSubscribeRetry();
                return;
            }

            if (!this.lastSubscribe) {
                return;
            }

            this.callbacks.onStatus('Connected (waiting for collection/snapshot)');
            this.sendSubscribe(this.lastSubscribe);
        }, 1_000);
    }

    private stopSubscribeRetry(): void {
        if (this.subscribeRetryHandle !== null) {
            clearInterval(this.subscribeRetryHandle);
            this.subscribeRetryHandle = null;
        }
    }
}
