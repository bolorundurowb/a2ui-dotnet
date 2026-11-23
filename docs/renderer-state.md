# Agent renderer state

Create one `RendererState` per peer session with the negotiated catalog registry. Serialize access. Pass the ordered, decoded surface messages for one atomic operation to `Prepare`. Validation uses a fork of the current Core session. It checks incomplete component graphs at the end of the batch. A diagnostic, cancellation, or disposal of the candidate preserves the previous committed state.

Call `Commit` on the validated turn before delivery. A candidate becomes stale when another turn commits. Read `NextMessage`, send it, and call `Acknowledge` after confirmed delivery. The committed mirror includes the entire turn. `EmittedCount` tracks its confirmed prefix. The next turn can commit once the prior turn has finished delivery.

After an interrupted send with a known delivery outcome, recover the cursor from `PendingDelivery` and continue with the first unacknowledged message. If delivery is ambiguous, the host must reconcile the receipt with its peer or establish a new session and recreate surfaces. The in-memory mirror cannot determine whether the peer received a message. The host owns persistent checkpoints, transport receipts, and reconnect policy.
