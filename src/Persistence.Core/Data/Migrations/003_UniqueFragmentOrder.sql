-- Guard against duplicate Order values within a working context. The in-memory context keys its
-- fragments by Order (a SortedList), so two junction rows sharing an Order in the same context would
-- silently overwrite one another on load — dropping a fragment. All in-process mutation keeps Order
-- unique; this index makes the invariant enforced rather than merely assumed.

CREATE UNIQUE INDEX IF NOT EXISTS uIdx_WorkingContextFragments_WorkingContextId_Order
    ON WorkingContextFragments(WorkingContextId, "Order");
