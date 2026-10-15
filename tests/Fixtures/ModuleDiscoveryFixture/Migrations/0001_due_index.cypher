CREATE INDEX review_due IF NOT EXISTS FOR (n:ReviewItem) ON (n.owner_id, n.next_review);
