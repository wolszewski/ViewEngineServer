export type RowData = Record<string, string | null>;

export interface SnapshotEvent {
    type: 'snapshot';
    totalCount: number;
    startIndex: number;
    rows: RowData[];
}

export interface RowUpdateEvent {
    type: 'rowUpdate';
    rowId: string;
    position: number;
    changedFields: RowData;
}

export interface RowInsertEvent {
    type: 'rowInsert';
    position: number;
    row: RowData;
}

export interface RowRemoveEvent {
    type: 'rowRemove';
    position: number;
}

export type DeltaEvent = SnapshotEvent | RowUpdateEvent | RowInsertEvent | RowRemoveEvent;

export interface SubscribeRequest {
    collectionId: string;
    sortColumn: string;
    pageSize: number;
    startIndex: number;
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

    public constructor(webSocketUrl: string, callbacks: ClientCallbacks) {
        this.webSocketUrl = webSocketUrl;
        this.callbacks = callbacks;
    }

    public connect(request: SubscribeRequest): void {
        if (this.socket && this.socket.readyState === WebSocket.OPEN) {
            this.sendSubscribe(request);
            return;
        }

        this.disconnect();
        this.lastSubscribe = request;
        this.hasReceivedSnapshot = false;
        this.callbacks.onStatus('Connecting...');

        const socket = new WebSocket(this.webSocketUrl);
        this.socket = socket;

        socket.addEventListener('open', () => {
            this.callbacks.onStatus('Connected');
            this.sendSubscribe(request);
            this.startSubscribeRetry();
        });

        socket.addEventListener('message', (event) => {
            const parsed = JSON.parse(String(event.data)) as DeltaEvent[];
            for (const delta of parsed) {
                if (delta.type === 'snapshot') {
                    this.hasReceivedSnapshot = true;
                    this.stopSubscribeRetry();
                    this.callbacks.onStatus('Connected');
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
        if (!this.socket || this.socket.readyState !== WebSocket.OPEN) {
            return;
        }

        this.socket.send(JSON.stringify({
            type: 'setViewport',
            startIndex,
            pageSize
        }));
    }

    public disconnect(): void {
        this.stopSubscribeRetry();
        const socket = this.socket;
        this.socket = null;
        this.hasReceivedSnapshot = false;
        if (socket) {
            socket.close();
        }
    }

    private sendSubscribe(request: SubscribeRequest): void {
        if (!this.socket || this.socket.readyState !== WebSocket.OPEN) {
            return;
        }

        this.lastSubscribe = request;
        this.socket.send(JSON.stringify({
            type: 'subscribe',
            collectionId: request.collectionId,
            sortColumn: request.sortColumn,
            sortAscending: true,
            startIndex: request.startIndex,
            pageSize: request.pageSize,
            filters: []
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
