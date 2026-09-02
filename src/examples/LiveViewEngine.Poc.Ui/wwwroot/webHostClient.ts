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
    onWaitingForCollection?: () => void;
}

interface PendingSnapshot {
    subscriptionId: number;
    startIndex: number;
    totalCount: number;
    rows: RowData[];
    isPartial: boolean;
    noChanges: boolean;
    firstMessageAt: number | null;
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
                this.sendUpdateView({
                    ...request,
                    sendSnapshot: true
                });
                this.lastSubscribe = request;
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
        this.hasReceivedSnapshot = false;
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
        this.stopSubscribeRetry();
        const socket = this.socket;
        this.socket = null;
        this.hasReceivedSnapshot = false;
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
                if (frame.snapshotFollows === 'none') {
                    this.hasReceivedSnapshot = true;
                    this.stopSubscribeRetry();
                    this.callbacks.onStatus('Connected');
                    this.pendingSnapshot = null;
                    this.snapshotRequestStartedAt = null;
                    return;
                }

                if (frame.snapshotFollows === 'pending') {
                    // The collection doesn't exist yet. The server remembers this subscription
                    // and will push the real snapshot automatically once the collection is
                    // created - no client polling/resend is needed, so stop the retry loop.
                    this.pendingSnapshot = null;
                    this.stopSubscribeRetry();
                    this.callbacks.onStatus('Connected (waiting for collection/snapshot)');
                    this.callbacks.onWaitingForCollection?.();
                    return;
                }

                this.pendingSnapshot = {
                    subscriptionId: frame.subscriptionId,
                    startIndex: frame.startIndex,
                    totalCount: frame.totalCount,
                    rows: [],
                    isPartial: false,
                    noChanges: false,
                    firstMessageAt: null
                };
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
                    noChanges: frame.noChanges === true,
                    firstMessageAt: performance.now()
                };
                if (!frame.isPartial) {
                    this.hasReceivedSnapshot = false;
                }
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
                    const noChanges = this.pendingSnapshot.noChanges;
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
                        isPartial,
                        noChanges
                    };

                    this.pendingSnapshot = null;
                    if (!isPartial) {
                        this.hasReceivedSnapshot = true;
                        this.stopSubscribeRetry();
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

        if (request.sortColumn) {
            message.sortColumn = request.sortColumn;
        }

        if (request.pageSize !== undefined) {
            message.pageSize = request.pageSize;
        }

        if (request.fields !== undefined && request.fields.length > 0) {
            message.fields = request.fields;
        }

        this.socket.send(JSON.stringify(message));
        if (request.sendSnapshot === false) {
            this.hasReceivedSnapshot = true;
            this.snapshotRequestStartedAt = null;
        } else {
            this.snapshotRequestStartedAt ??= performance.now();
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
            pageSize: request.pageSize,
            sortColumn: request.sortColumn,
            sortAscending: request.sortAscending
        }

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

        if (request.sendSnapshot !== undefined) {
            message.sendSnapshot = request.sendSnapshot;
        }

        this.socket.send(JSON.stringify(message));
        this.hasReceivedSnapshot = false;
        this.snapshotRequestStartedAt = performance.now();
        this.startSubscribeRetry();
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

            if (this.pendingSnapshot !== null) {
                // A snapshot is already streaming in (e.g. a large all-rows transfer) - don't
                // send redundant retries while we're actively receiving it.
                return;
            }

            if (!this.lastSubscribe) {
                return;
            }

            this.callbacks.onStatus('Connected (waiting for collection/snapshot)');
            this.callbacks.onWaitingForCollection?.();
            if (this.activeSubscriptionId !== null) {
                this.sendUpdateView(this.lastSubscribe);
            } else {
                this.sendSubscribe(this.lastSubscribe);
            }
        }, 1_000);
    }

    private stopSubscribeRetry(): void {
        if (this.subscribeRetryHandle !== null) {
            clearInterval(this.subscribeRetryHandle);
            this.subscribeRetryHandle = null;
        }
    }
}
