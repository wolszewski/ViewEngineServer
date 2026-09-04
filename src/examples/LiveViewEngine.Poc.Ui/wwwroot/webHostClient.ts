import { parseCompactFrame } from './compactProtocol';
import { parseJsonFrame } from './jsonProtocol';
import type {
    DeltaEvent,
    MessageFormat,
    ProtocolFrame,
    RowData,
    RowInsertEvent,
    RowReplaceEvent,
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
    RowReplaceEvent,
    RowRemoveEvent,
    RowUpdateEvent,
    SnapshotEvent,
    SubscribeRequest
} from './webHostClient.types';

interface ClientCallbacks {
    onStatus: (status: string) => void;
    onEvent: (event: DeltaEvent) => void;
    onSubscriptionRejected?: (reason: string, message: string) => void;
    // Non-terminal: the subscription stays alive server-side with its previous view/viewport
    // untouched - only the requested updateview/setviewport change was refused. Unlike
    // onSubscriptionRejected, this must NOT be treated as "the subscription died" - do not stop
    // listening for further live deltas.
    onUpdateRejected?: (reason: string, message: string) => void;
}

interface PendingSnapshot {
    subscriptionId: number;
    startIndex: number;
    totalCount: number;
    rows: RowData[];
    isPartial: boolean;
    firstMessageAt: number | null;
}

export class WebHostClient {
    private readonly webSocketUrl: string;
    private readonly callbacks: ClientCallbacks;

    private socket: WebSocket | null = null;
    private lastSubscribe: SubscribeRequest | null = null;
    private activeSubscriptionId: number | null = null;
    private currentFields: string[] = [];
    private currentMessageFormat: MessageFormat = 'compact';
    private pendingSnapshot: PendingSnapshot | null = null;
    private snapshotRequestStartedAt: number | null = null;

    public constructor(webSocketUrl: string, callbacks: ClientCallbacks) {
        this.webSocketUrl = webSocketUrl;
        this.callbacks = callbacks;
    }

    public get isConnected(): boolean {
        return this.socket?.readyState === WebSocket.OPEN;
    }

    public connect(request: SubscribeRequest): void {
        if (this.socket && this.socket.readyState === WebSocket.OPEN) {
            const sameCollection = this.lastSubscribe?.collectionId === request.collectionId;
            const sameMessageFormat = (this.lastSubscribe?.messageFormat ?? 'compact') === (request.messageFormat ?? 'compact');
            if (this.activeSubscriptionId !== null && sameCollection && sameMessageFormat) {
                this.sendUpdateView(request);
            } else {
                if (this.activeSubscriptionId !== null) {
                    this.sendUnsubscribe(this.activeSubscriptionId);
                }

                this.sendSubscribe(request);
            }
            return;
        }

        this.disconnect();
        this.lastSubscribe = request;
        this.activeSubscriptionId = null;
        this.currentFields = [];
        this.currentMessageFormat = request.messageFormat ?? 'compact';
        this.pendingSnapshot = null;
        this.snapshotRequestStartedAt = null;
        this.callbacks.onStatus('Connecting...');

        const socket = new WebSocket(this.webSocketUrl);
        this.socket = socket;

        socket.addEventListener('open', () => {
            this.callbacks.onStatus('Connected');
            const subscribe = this.lastSubscribe ?? request;
            this.sendSubscribe(subscribe);
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
            this.currentFields = [];
            this.pendingSnapshot = null;
            this.snapshotRequestStartedAt = null;
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

        this.sendUpdateView({
            ...this.lastSubscribe,
            startIndex,
            pageSize
        });
    }

    public disconnect(): void {
        const socket = this.socket;
        this.socket = null;
        this.activeSubscriptionId = null;
        this.currentFields = [];
        this.pendingSnapshot = null;
        this.snapshotRequestStartedAt = null;
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
                    this.callbacks.onStatus('Connected');
                    this.pendingSnapshot = null;
                    this.snapshotRequestStartedAt = null;
                    return;
                }

                this.pendingSnapshot = {
                    subscriptionId: frame.subscriptionId,
                    startIndex: frame.startIndex,
                    totalCount: frame.totalCount,
                    rows: [],
                    isPartial: false,
                    firstMessageAt: null
                };
                return;
            case 'rejected':
                this.activeSubscriptionId = null;
                this.pendingSnapshot = null;
                this.snapshotRequestStartedAt = null;
                this.callbacks.onStatus('Subscription failed');
                this.callbacks.onSubscriptionRejected?.(frame.reason, frame.message);
                return;
            case 'updateRejected':
                // Deliberately does not touch activeSubscriptionId/pendingSnapshot - the server keeps
                // this subscription alive with its previous view/viewport, so subsequent live deltas
                // (rowInsert/rowUpdate/rowRemove/rowReplace) for this subscriptionId must keep being
                // processed normally.
                this.callbacks.onUpdateRejected?.(frame.reason, frame.message);
                return;
            case 'snapshotStart':
                if (!this.isActiveSubscription(frame.subscriptionId)) {
                    return;
                }
 
                if (frame.fields && frame.fields.length > 0) {
                    this.currentFields = frame.fields;
                }
 
                this.pendingSnapshot = {
                    subscriptionId: frame.subscriptionId,
                    startIndex: frame.startIndex,
                    totalCount: frame.totalCount,
                    rows: [],
                    isPartial: frame.isPartial === true,
                    firstMessageAt: performance.now()
                };
                return;
            case 'snapshotRow':
                if (!this.isActiveSubscription(frame.subscriptionId) || !this.pendingSnapshot) {
                    return;
                }

                this.pendingSnapshot.firstMessageAt ??= performance.now();
                this.pendingSnapshot.rows.push(frame.row);
                return;
            case 'eos':
                if (!this.isActiveSubscription(frame.subscriptionId) || !this.pendingSnapshot) {
                    return;
                }

                {
                    const isPartial = this.pendingSnapshot.isPartial;
                    const now = performance.now();
                    const firstMessageAt = this.pendingSnapshot.firstMessageAt ?? now;
                    const waitMs = this.snapshotRequestStartedAt === null
                        ? 0
                        : Math.max(0, firstMessageAt - this.snapshotRequestStartedAt);
                    const transferMs = Math.max(0, now - firstMessageAt);
                    this.snapshotRequestStartedAt = null;
                    const snapshot: SnapshotEvent = {
                        type: 'snapshot',
                        subscriptionId: frame.subscriptionId,
                        totalCount: this.pendingSnapshot.totalCount,
                        startIndex: this.pendingSnapshot.startIndex,
                        rows: this.pendingSnapshot.rows,
                        waitMs,
                        transferMs,
                        isPartial
                    };

                    this.pendingSnapshot = null;
                    if (!isPartial) {
                        this.callbacks.onStatus('Connected');
                    }
                    this.callbacks.onEvent(snapshot);
                }
                return;
            case 'rowInsert':
            case 'rowUpdate':
            case 'rowRemove':
            case 'rowReplace':
                if (!this.isActiveSubscription(frame.event.subscriptionId)) {
                    return;
                }

                this.callbacks.onEvent(frame.event);
                return;
        }
    }

    private isActiveSubscription(subscriptionId: number): boolean {
        return this.activeSubscriptionId !== null && this.activeSubscriptionId === subscriptionId;
    }

    private sendSubscribe(request: SubscribeRequest): void {
        if (!this.socket || this.socket.readyState !== WebSocket.OPEN) {
            return;
        }

        this.activeSubscriptionId = null;
        this.lastSubscribe = request;
        this.currentMessageFormat = request.messageFormat ?? 'compact';
        const message: Record<string, unknown> = {
            type: 'subscribe',
            collectionId: request.collectionId,
            sortColumn: request.sortColumn,
            sortAscending: request.sortAscending,
            startIndex: request.startIndex,
            sendSnapshot: request.sendSnapshot !== false,
            messageFormat: this.currentMessageFormat,
            filters: (request.filters ?? []).map((filter) => ({
                field: filter.field,
                operator: filter.operator,
                value: filter.value
            }))
        };

        if (request.fields !== undefined && request.fields.length > 0) {
            message.fields = request.fields;
        }

        if (typeof request.pageSize === 'number') {
            message.pageSize = request.pageSize;
        }

        this.socket.send(JSON.stringify(message));
        if (request.sendSnapshot !== false) {
            this.snapshotRequestStartedAt ??= performance.now();
        } else {
            this.snapshotRequestStartedAt = null;
        }
    }

    private sendUpdateView(request: SubscribeRequest): void {
        if (!this.socket || this.socket.readyState !== WebSocket.OPEN || this.activeSubscriptionId === null) {
            return;
        }

        this.lastSubscribe = request;
        const message: Record<string, unknown> = {
            type: 'updateview',
            subscriptionId: this.activeSubscriptionId,
            startIndex: request.startIndex,
            sortColumn: request.sortColumn,
            sortAscending: request.sortAscending
        };

        if (request.filters !== undefined) {
            message.filters = request.filters.map((filter) => ({
                field: filter.field,
                operator: filter.operator,
                value: filter.value
            }));
        }

        if (request.fields !== undefined) {
            message.fields = request.fields;
        }

        if (typeof request.pageSize === 'number') {
            message.pageSize = request.pageSize;
        }

        this.socket.send(JSON.stringify(message));
        this.snapshotRequestStartedAt = performance.now();
    }

    private sendUnsubscribe(subscriptionId: number): void {
        if (!this.socket || this.socket.readyState !== WebSocket.OPEN) {
            return;
        }

        this.socket.send(JSON.stringify({
            type: 'unsubscribe',
            subscriptionId
        }));
    }
}
