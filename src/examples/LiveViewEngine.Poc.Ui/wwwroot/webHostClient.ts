import { parseCompactFrame } from './compactProtocol';
import { parseJsonFrame } from './jsonProtocol';
import type {
    DeltaEvent,
    MessageFormat,
    ProtocolFrame,
    RowData,
    RowInsertEvent,
    RowRemoveEvent,
    RowUpdateEvent,
    SnapshotEvent,
    SubscribeRequest
} from './webHostClient.types';

export type {
    DeltaEvent,
    MessageFormat,
    RowData,
    RowInsertEvent,
    RowRemoveEvent,
    RowUpdateEvent,
    SnapshotEvent,
    SubscribeRequest
} from './webHostClient.types';

interface ClientCallbacks {
    onStatus: (status: string) => void;
    onEvent: (event: DeltaEvent) => void;
}

interface PendingSnapshot {
    subscriptionId: number;
    startIndex: number;
    totalCount: number;
    rows: RowData[];
}

export class WebHostClient {
    private readonly webSocketUrl: string;
    private readonly callbacks: ClientCallbacks;

    private socket: WebSocket | null = null;
    private subscribeRetryHandle: number | null = null;
    private hasReceivedSnapshot = false;
    private lastSubscribe: SubscribeRequest | null = null;
    private activeSubscriptionId: number | null = null;
    private currentFields: string[] = [];
    private currentMessageFormat: MessageFormat = 'compact';
    private pendingSnapshot: PendingSnapshot | null = null;

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
        this.currentFields = [];
        this.currentMessageFormat = request.messageFormat ?? 'compact';
        this.pendingSnapshot = null;
        this.callbacks.onStatus('Connecting...');

        const socket = new WebSocket(this.webSocketUrl);
        this.socket = socket;

        socket.addEventListener('open', () => {
            this.callbacks.onStatus('Connected');
            const subscribe = this.lastSubscribe ?? request;
            this.sendSubscribe(subscribe);
            if (subscribe.sendSnapshot !== false) {
                this.startSubscribeRetry();
            }
        });

        socket.addEventListener('message', (event) => {
            const frameText = String(event.data);
            const frames = this.currentMessageFormat === 'json' || frameText.trimStart().startsWith('{')
                ? parseJsonFrame(frameText)
                : parseCompactFrame(frameText, this.currentFields);
            for (const frame of frames) {
                this.handleFrame(frame);
            }
        });

        socket.addEventListener('close', () => {
            this.stopSubscribeRetry();
            this.hasReceivedSnapshot = false;
            this.currentFields = [];
            this.pendingSnapshot = null;
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

        if (!this.socket || this.socket.readyState !== WebSocket.OPEN || !this.lastSubscribe) {
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
        this.currentFields = [];
        this.pendingSnapshot = null;
        if (socket) {
            socket.close();
        }
    }

    private handleFrame(frame: ProtocolFrame): void {
        switch (frame.kind) {
            case 'accepted':
                this.activeSubscriptionId = frame.subscriptionId;
                this.currentFields = frame.fields;
                if (!frame.snapshotFollows) {
                    this.hasReceivedSnapshot = true;
                    this.stopSubscribeRetry();
                    this.callbacks.onStatus('Connected');
                    this.pendingSnapshot = null;
                    return;
                }

                this.pendingSnapshot = {
                    subscriptionId: frame.subscriptionId,
                    startIndex: frame.startIndex,
                    totalCount: frame.totalCount,
                    rows: []
                };
                return;
            case 'snapshotStart':
                if (!this.isActiveSubscription(frame.subscriptionId)) {
                    return;
                }

                this.pendingSnapshot = {
                    subscriptionId: frame.subscriptionId,
                    startIndex: frame.startIndex,
                    totalCount: frame.totalCount,
                    rows: []
                };
                this.hasReceivedSnapshot = false;
                return;
            case 'snapshotRow':
                if (!this.isActiveSubscription(frame.subscriptionId) || !this.pendingSnapshot) {
                    return;
                }

                this.pendingSnapshot.rows.push(frame.row);
                return;
            case 'eos':
                if (!this.isActiveSubscription(frame.subscriptionId) || !this.pendingSnapshot) {
                    return;
                }

                {
                    const snapshot: SnapshotEvent = {
                        type: 'snapshot',
                        subscriptionId: frame.subscriptionId,
                        totalCount: this.pendingSnapshot.totalCount,
                        startIndex: this.pendingSnapshot.startIndex,
                        rows: this.pendingSnapshot.rows
                    };

                    this.pendingSnapshot = null;
                    this.hasReceivedSnapshot = true;
                    this.stopSubscribeRetry();
                    this.callbacks.onStatus('Connected');
                    this.ensureSnapshotMatchesRequestedViewport(snapshot);
                    this.callbacks.onEvent(snapshot);
                }
                return;
            case 'rowInsert':
            case 'rowUpdate':
            case 'rowRemove':
                if (this.activeSubscriptionId !== null && frame.event.subscriptionId !== this.activeSubscriptionId) {
                    return;
                }

                this.callbacks.onEvent(frame.event);
                return;
        }
    }

    private isActiveSubscription(subscriptionId: number): boolean {
        return this.activeSubscriptionId === null || this.activeSubscriptionId === subscriptionId;
    }

    private sendSubscribe(request: SubscribeRequest): void {
        if (!this.socket || this.socket.readyState !== WebSocket.OPEN) {
            return;
        }

        this.lastSubscribe = request;
        this.currentMessageFormat = request.messageFormat ?? 'compact';
        const message: Record<string, unknown> = {
            type: 'subscribe',
            collectionId: request.collectionId,
            sortColumn: request.sortColumn,
            sortAscending: request.sortAscending,
            startIndex: request.startIndex,
            pageSize: request.pageSize,
            sendSnapshot: request.sendSnapshot !== false,
            messageFormat: this.currentMessageFormat,
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
        if (request.sendSnapshot === false) {
            this.hasReceivedSnapshot = true;
        }
    }

    private ensureSnapshotMatchesRequestedViewport(snapshot: SnapshotEvent): void {
        if (!this.lastSubscribe || snapshot.startIndex === this.lastSubscribe.startIndex) {
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
        this.hasReceivedSnapshot = false;
        this.startSubscribeRetry();
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
